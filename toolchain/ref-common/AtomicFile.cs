using System;
using System.IO;

namespace DotKt.Toolchain;

/// <summary>
/// Atomic file writes for the toolchain's assembly emitters (#52). An in-place truncate-then-write (FileMode.Create)
/// is observable by a concurrent reader as a partial/torn image — the root cause of the flaky
/// "retarget: Format of the executable (.dll) is invalid" / BadImageFormatException seen when one stage rewrites a dll
/// (ilemit emit, retarget in-place repoint) while another (retarget/dll2klib/bir2cir) is loading the SAME file. We
/// write to a sibling temp file and rename over the target: a same-directory rename is atomic on every OS the
/// toolchain runs on, so a reader always sees either the whole old file or the whole new one — never a mid-write.
/// </summary>
static class AtomicFile
{
    /// <summary>Write <paramref name="path"/> atomically: fill a sibling temp file via <paramref name="write"/>, then rename over the target.</summary>
    public static void Write(string path, Action<FileStream> write)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        // Sibling temp (same directory ⇒ same filesystem ⇒ rename is atomic, not a cross-device copy). PID + a
        // GUID fragment keeps concurrent writers of the same target from colliding on the temp name.
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                write(fs);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
            throw;
        }
    }

    /// <summary>Write text to <paramref name="path"/> atomically (temp + rename).</summary>
    public static void WriteAllText(string path, string contents) =>
        Write(path, fs => { using var w = new StreamWriter(fs); w.Write(contents); });
}
