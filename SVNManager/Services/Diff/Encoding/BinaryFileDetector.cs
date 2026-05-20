using System.Text;

namespace SVNManager;

internal static class BinaryFileDetector
{
    private const int SampleSize = 8192;

    public static BinaryFileInfo Inspect(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new BinaryFileInfo(filePath, 0, false);
        }

        var info = new FileInfo(filePath);
        var length = (int)Math.Min(SampleSize, info.Length);
        if (length == 0)
        {
            return new BinaryFileInfo(filePath, info.Length, false);
        }

        var bytes = new byte[length];
        using (var stream = File.OpenRead(filePath))
        {
            _ = stream.Read(bytes, 0, bytes.Length);
        }

        return new BinaryFileInfo(filePath, info.Length, IsBinarySample(bytes));
    }

    private static bool IsBinarySample(byte[] bytes)
    {
        if (HasTextBom(bytes))
        {
            return false;
        }

        if (bytes.Any(value => value == 0))
        {
            return !LooksLikeUtf16WithoutBom(bytes);
        }

        var suspicious = 0;
        foreach (var value in bytes)
        {
            if (value is 9 or 10 or 13)
            {
                continue;
            }

            if (value < 32 || value == 127)
            {
                suspicious++;
            }
        }

        return bytes.Length > 0 && (double)suspicious / bytes.Length > 0.30;
    }

    private static bool HasTextBom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ||
            bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ||
            bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF;
    }

    private static bool LooksLikeUtf16WithoutBom(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        var evenZeros = 0;
        var oddZeros = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != 0)
            {
                continue;
            }

            if (index % 2 == 0)
            {
                evenZeros++;
            }
            else
            {
                oddZeros++;
            }
        }

        var half = bytes.Length / 2.0;
        return evenZeros / half > 0.35 || oddZeros / half > 0.35;
    }
}

internal sealed record BinaryFileInfo(string Path, long Size, bool IsBinary);
