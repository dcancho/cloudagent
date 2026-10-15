using System.IO;
using System.Text;

namespace CloudAgent.Host;

/// <summary>Writes to the console and to a log file at once, so the host's log survives the window.</summary>
public sealed class TeeTextWriter(TextWriter console, TextWriter file) : TextWriter
{
    private static readonly object Gate = new();

    public override Encoding Encoding => console.Encoding;

    public override void Write(char value)
    {
        lock (Gate)
        {
            console.Write(value);
            file.Write(value);
        }
    }

    public override void Write(string? value)
    {
        lock (Gate)
        {
            console.Write(value);
            file.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (Gate)
        {
            console.WriteLine(value);
            file.WriteLine(value);
            file.Flush();
        }
    }

    public override void Flush()
    {
        lock (Gate)
        {
            console.Flush();
            file.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            file.Dispose();
        base.Dispose(disposing);
    }
}
