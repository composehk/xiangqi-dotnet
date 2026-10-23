using System.Collections.Concurrent;
using System.Diagnostics;
using Xiangqi.Core;

namespace Xiangqi.Engine;

/// <summary>
/// UCI process client shaped for Pikafish. Launches the engine, performs the
/// uci/uciok and isready/readyok handshake, and parses bestmove.
/// </summary>
public sealed class UciEngine : IEngine, IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly BlockingCollection<string> _lines = new();
    private readonly object _writeLock = new();
    private bool _disposed;

    public UciEngine(string enginePath, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(enginePath))
            throw new ArgumentException("Engine path is required.", nameof(enginePath));

        var start = new ProcessStartInfo
        {
            FileName = enginePath,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!_process.Start())
            throw new InvalidOperationException($"Failed to start UCI engine '{enginePath}'.");

        _stdin = _process.StandardInput;
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        Task.Run(ReadStdout);

        try
        {
            Send("uci");
            WaitFor(line => line == "uciok", 5_000);
            Send("isready");
            WaitFor(line => line == "readyok", 5_000);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string EnginePath => _process.StartInfo.FileName;

    public async Task<EngineResult> SearchAsync(Board board, SearchLimits limits, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(board);

        var stopwatch = Stopwatch.StartNew();
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                Send("stop");
            }
            catch (InvalidOperationException)
            {
                // The process has already exited.
            }
        });

        await Task.Yield();
        Send("ucinewgame");
        Send("isready");
        WaitFor(line => line == "readyok", 5_000, cancellationToken);
        Send("position fen " + board.ToFen());
        Send(BuildGo(limits));

        int timeout = limits.TimeMs > 0 ? limits.TimeMs + 10_000 : 60_000;
        string line = WaitFor(text => text.StartsWith("bestmove", StringComparison.Ordinal), timeout, cancellationToken);
        Move best = ParseBestMove(line);
        return new EngineResult(best, 0, limits.Depth, stopwatch.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                Send("quit");
                if (!_process.WaitForExit(300))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        finally
        {
            _process.Dispose();
            _lines.CompleteAdding();
        }
    }

    private void ReadStdout()
    {
        try
        {
            while (_process.StandardOutput.ReadLine() is { } line)
                _lines.Add(line);
        }
        catch (Exception)
        {
            // The process closed the pipe.
        }
        finally
        {
            _lines.CompleteAdding();
        }
    }

    private void Send(string command)
    {
        lock (_writeLock)
        {
            _stdin.WriteLine(command);
            _stdin.Flush();
        }
    }

    private string WaitFor(Func<string, bool> match, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int slice = (int)Math.Min(200, timeoutMs - stopwatch.ElapsedMilliseconds);
            if (slice <= 0)
                break;

            try
            {
                if (_lines.TryTake(out string? line, slice, cancellationToken) && line != null && match(line))
                    return line;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        throw new TimeoutException("UCI engine did not respond in time.");
    }

    private static string BuildGo(SearchLimits limits)
    {
        int depth = limits.Depth < 1 ? 0 : limits.Depth;
        int time = limits.TimeMs < 0 ? 0 : limits.TimeMs;
        if (depth > 0 && time > 0)
            return $"go depth {depth} movetime {time}";
        if (depth > 0)
            return $"go depth {depth}";
        if (time > 0)
            return $"go movetime {time}";
        return "go depth 4";
    }

    private static Move ParseBestMove(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "bestmove")
            throw new InvalidOperationException($"Unexpected UCI reply '{line}'.");

        string text = parts[1];
        if (text is "(none)" or "0000" or "none")
            return Move.None;
        return Move.Parse(text);
    }
}
