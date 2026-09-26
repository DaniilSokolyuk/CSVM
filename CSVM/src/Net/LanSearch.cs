using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// A guest's search for open doors on its network. Each <see cref="Ask"/> sends one query under a
/// fresh token and starts a round; <see cref="Poll"/> reads the answers to that round. A game silent
/// through both the last round and the current one is dropped. So a host that closed leaves the
/// list on the next round. Engine-free: the socket is handed in.
/// </summary>
public sealed class LanSearch : IDisposable
{
    /// <summary>The most datagrams one poll reads, so a flood costs a bounded amount per frame.
    /// </summary>
    public const int RepliesPerPoll = 64;

    /// <summary>The most games the list holds. A network with more open doors than this shows
    /// the first ones that answered.</summary>
    public const int MaxGames = 64;

    private readonly ILanSocket _socket;
    private readonly string _address;
    private readonly int _port;
    private readonly Random _random;
    private readonly byte[] _query = new byte[LanDiscovery.Size];
    private readonly List<(LanGame Game, int Round)> _games = new();
    private uint _token;
    private int _round;

    /// <summary>A search over <paramref name="socket"/>, which it owns from here on. Its queries go
    /// to <paramref name="address"/> on <paramref name="port"/>: the broadcast address on a real
    /// network, the loopback in a suite. <paramref name="random"/> draws the tokens.</summary>
    public LanSearch(ILanSocket socket, string address, int port, Random? random = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _address = string.IsNullOrWhiteSpace(address) ? throw new ArgumentException("a search needs an address", nameof(address)) : address;
        _port = port;
        _random = random ?? new Random();
    }

    /// <summary>How many rounds this search has asked.</summary>
    public int Rounds => _round;

    /// <summary>The games heard in the current or the last round, in the order they first
    /// answered.</summary>
    public IReadOnlyList<LanGame> Games
    {
        get
        {
            var games = new List<LanGame>(_games.Count);
            foreach (var (game, _) in _games)
            {
                games.Add(game);
            }

            return games;
        }
    }

    /// <summary>Starts a round: drops the games that did not answer the last one and sends a query
    /// under a fresh token.</summary>
    public void Ask()
    {
        _games.RemoveAll(entry => entry.Round < _round);
        _round++;
        _token = (uint)_random.Next(1, int.MaxValue);
        int length = LanDiscovery.WriteQuery(_query, _token);
        _socket.Send(_address, _port, _query.AsSpan(0, length));
    }

    /// <summary>Reads the answers waiting. An answer to an earlier round, or anything that is not
    /// an answer, is read and dropped.</summary>
    public void Poll()
    {
        for (int i = 0; i < RepliesPerPoll; i++)
        {
            byte[]? datagram = _socket.Receive(out string address, out _);
            if (datagram == null)
            {
                return;
            }

            if (_round == 0 || !LanDiscovery.TryReadReply(datagram, _token, out int gamePort, out var advert))
            {
                continue;
            }

            Heard(new LanGame(address, gamePort, advert));
        }
    }

    /// <summary>Closes the socket.</summary>
    public void Dispose() => _socket.Dispose();

    private void Heard(LanGame game)
    {
        for (int i = 0; i < _games.Count; i++)
        {
            if (_games[i].Game.Address == game.Address && _games[i].Game.Port == game.Port)
            {
                _games[i] = (game, _round);
                return;
            }
        }

        if (_games.Count < MaxGames)
        {
            _games.Add((game, _round));
        }
    }
}
