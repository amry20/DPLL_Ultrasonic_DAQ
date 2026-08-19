using System.Globalization;
using System.Text;
using DPLL_Ultrasonic_DAQ.Models;

namespace DPLL_Ultrasonic_DAQ.Services;

/// <summary>
/// Background CSV data logger.
/// Persists every telemetry sample received while logging is active to
/// <c>data/&lt;yyyyMMdd_HHmmss&gt;.csv</c>. The filename is fixed at the moment
/// the recording starts (the host clock time of the Start button press).
/// Thread-safe: <see cref="LogTelemetry"/> may be called from any thread.
/// </summary>
public sealed class CsvLoggerService : IDisposable
{
    private readonly object _gate = new();
    private readonly string _dataDir;
    private StreamWriter? _writer;
    private string? _fileName;
    private DateTimeOffset _startedAt;
    private long _rowCount;

    public CsvLoggerService()
    {
        _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>True while a CSV recording session is in progress.</summary>
    public bool IsLogging { get; private set; }

    /// <summary>Absolute path of the currently open CSV file (null when idle).</summary>
    public string? FilePath => _fileName;

    /// <summary>UTC time at which the current recording started.</summary>
    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>Number of rows written since recording started.</summary>
    public long RowCount => _rowCount;

    /// <summary>
    /// Begin a new CSV recording. If a recording is already active it is left
    /// untouched and <c>false</c> is returned.
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (IsLogging) return false;

            Directory.CreateDirectory(_dataDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(_dataDir, $"{stamp}.csv");

            _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine(
                "Timestamp_ms,ReferenceFrequencyHz,PhaseErrorNs,DACVoltage_V," +
                "LockStatus,LockState,PhaseStale,IsLocked");
            _writer.Flush();

            _fileName = path;
            _startedAt = DateTimeOffset.UtcNow;
            _rowCount = 0;
            IsLogging = true;
            return true;
        }
    }

    /// <summary>
    /// Append one telemetry sample to the open CSV file (no-op when idle).
    /// </summary>
    public void LogTelemetry(DpllTelemetry t)
    {
        lock (_gate)
        {
            if (!IsLogging || _writer is null) return;

            // t.Timestamp is seconds since the Unix epoch (UTC) — convert to ms.
            var tsMs = (long)Math.Round(t.Timestamp * 1000.0);
            _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{tsMs},{t.ReferenceFrequencyHz.ToString("F3", CultureInfo.InvariantCulture)}," +
                $"{t.PhaseErrorNs.ToString("F1", CultureInfo.InvariantCulture)}," +
                $"{t.DACVoltage_V.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{t.LockStatus},{t.State},{t.PhaseStale},{(t.IsLocked ? 1 : 0)}"));
            _rowCount++;
        }
    }

    /// <summary>
    /// Stop the current recording and close the file. Returns the file path if
    /// a session was active, otherwise null.
    /// </summary>
    public string? Stop()
    {
        lock (_gate)
        {
            if (!IsLogging) return null;

            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            var path = _fileName;
            _fileName = null;
            IsLogging = false;
            return path;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            IsLogging = false;
        }
    }
}
