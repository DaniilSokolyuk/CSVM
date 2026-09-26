using System;
using CSVM.Net;
using CSVM.Utils;

namespace CSVM.Session.World;

/// <summary>
/// The cutscene skip over the wire. Every end plays each shared episode, and any player's skip
/// ends it on every machine, the splitscreen rule carried across the link. The host decides: a
/// guest's skip is an ask sent to it, and the host skips and tells every guest. Each
/// <see cref="CutsceneSkipMessage"/> names one episode by definition key and ordinal. A skip can
/// then only end the film it was pressed in (<c>docs/org/multiplayer-messages.md</c>).
/// </summary>
internal sealed class NetCutsceneLink
{
    private readonly NetSession _net;
    private readonly CutsceneController _cutscene;
    private readonly Func<int, int> _seatOf;

    private NetCutsceneLink(NetSession net, CutsceneController cutscene, Func<int, int> seatOf)
    {
        _net = net;
        _cutscene = cutscene;
        _seatOf = seatOf;
    }

    /// <summary>Skips this end sent: a guest's asks, or the host's decisions.</summary>
    internal int SkipsSent { get; private set; }

    /// <summary>Wires <paramref name="cutscene"/> to <paramref name="net"/>.
    /// <paramref name="seatOf"/> turns a local pane index (the skipper the controller reports)
    /// into that human's seat.</summary>
    internal static NetCutsceneLink Open(NetSession net, CutsceneController cutscene, Func<int, int> seatOf)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(cutscene);
        ArgumentNullException.ThrowIfNull(seatOf);
        var link = new NetCutsceneLink(net, cutscene, seatOf);
        if (net.IsHost)
        {
            link.Decide();
        }
        else
        {
            link.Ask();
        }

        return link;
    }

    private void Decide()
    {
        // Raised for the host's own skips and for a guest's ask it took. One broadcast per episode
        // ended, which the asking guest waits on too.
        _cutscene.Skipped = (skipper, key, ordinal, asked) =>
        {
            SkipsSent++;
            _net.Broadcast(new CutsceneSkipMessage((byte)(asked ? skipper : _seatOf(skipper)),
                (ushort)ordinal, key), NetChannels.Events);
        };
        // ⚠ Only the seat's own machine asks for it. The ordinal still guards a late ask: the
        // episode it names is over, so it is dropped rather than ending the next one.
        _net.On<CutsceneSkipMessage>((peer, message) =>
        {
            if (_net.PeerOfSeat(message.Seat) != peer)
            {
                Log.Warn("anim", $"net cutscene: a skip for seat {message.Seat} from a peer that does not fly it, ignored");
                return;
            }

            _cutscene.TakeSkip(message.Key, message.Episode, message.Seat);
        });
    }

    private void Ask()
    {
        _cutscene.SkipAsked = (skipper, key, ordinal) =>
        {
            SkipsSent++;
            _net.Send(_net.HostPeer, new CutsceneSkipMessage((byte)_seatOf(skipper), (ushort)ordinal, key),
                NetChannels.Events);
        };
        _net.On<CutsceneSkipMessage>((_, message) =>
            _cutscene.TakeSkip(message.Key, message.Episode, message.Seat));
    }
}
