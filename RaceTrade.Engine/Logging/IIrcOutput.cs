using System.Collections.Generic;
using RaceTrade.Engine.Compat;

namespace RaceTrade.Engine.Logging
{
    /// <summary>
    /// Replaces the WinForms <c>IrcLog</c> form that the IRC clients and the racer used
    /// to write to directly. The engine only ever needed two things from it — append a
    /// line, and know whether it is still alive — so that is all this exposes.
    /// </summary>
    public interface IIrcOutput
    {
        void AppendLog(string message, Color color);

        /// <summary>
        /// True when the sink can no longer accept output (window closed, client gone).
        /// Named after the WinForms member so ported call sites read unchanged.
        /// </summary>
        bool IsDisposed { get; }
    }

    /// <summary>
    /// Replaces the WinForms <c>TabbedIrcLog</c> form: per-channel chat output.
    ///
    /// The WinForms version required marshalling to the UI thread, which is why the
    /// original code checked InvokeRequired/IsHandleCreated and called BeginInvoke.
    /// That is a UI concern: implementations are now responsible for their own
    /// thread-safety, and the engine simply calls these methods from whatever thread
    /// it is on. Implementations MUST NOT block — this runs on the IRC receive path.
    /// </summary>
    public interface IChannelOutput
    {
        void AppendChannelMessage(string siteName, string channelName, string message, Color color);

        /// <summary>Ensures a tab/room exists for this site + channel.</summary>
        void EnsureChannel(string siteName, string channelName);

        // Channel user tracking (JOIN/PART/QUIT/NICK and the 353 NAMES reply).
        void AddUser(string siteName, string channelName, string username);
        void RemoveUser(string siteName, string channelName, string username);
        void UpdateUserList(string siteName, string channelName, List<string> users);

        bool IsDisposed { get; }
    }
}
