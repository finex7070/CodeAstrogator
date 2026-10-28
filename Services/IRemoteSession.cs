using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// A Remote Control session of the current chat. Two implementations: <see cref="BackgroundRemoteSession"/>
    /// (CLI 2.1.29x+: a background session hosted by the CLI's daemon — no window, link/QR in the panel, live
    /// status, clean stop) and <see cref="RemoteTerminalLauncher"/> (fallback for CLIs without <c>--bg</c>:
    /// an interactive PowerShell console that shows the link itself).
    /// </summary>
    internal interface IRemoteSession : IDisposable
    {
        bool IsActive { get; }

        /// <summary>UTC start time — lower bound for the session discovery when the session ends.</summary>
        DateTime StartedUtc { get; }

        string? LastError { get; }

        /// <summary>The Remote Control link (claude.ai/code/session_…) once known; null for the console variant.</summary>
        string? Url { get; }

        /// <summary>The CLI session id the remote session works on, once known.</summary>
        string? SessionId { get; }

        /// <summary><c>busy</c> / <c>idle</c> while known.</summary>
        string? Activity { get; }

        /// <summary>The session can additionally be opened in a terminal (<see cref="OpenTerminal"/>).</summary>
        bool CanOpenTerminal { get; }

        /// <summary>Raised once when the session ends (closed, stopped, gone).</summary>
        event Action? Ended;

        /// <summary>Raised when <see cref="Url"/>, <see cref="Activity"/> or <see cref="SessionId"/> changes, and
        /// on every status poll (the bridge mirrors the transcript from it). Background thread.</summary>
        event Action? Changed;

        Task<bool> StartAsync(string claudeExe, string? resumeSessionId, string? workingDirectory, string? name, CancellationToken ct);

        Task EndAsync(CancellationToken ct);

        void OpenTerminal();
    }
}
