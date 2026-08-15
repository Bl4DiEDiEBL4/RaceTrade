using System;
using System.Text;

namespace RaceTrade;

public static class SqliteRuntime
{
    private static readonly object InitLock = new();
    private static bool initialized;

    public static void EnsureInitialized()
    {
        if (initialized) return;

        lock (InitLock)
        {
            if (initialized) return;

            SQLitePCL.Batteries_V2.Init();
            initialized = true;
        }
    }

    public static string DescribeException(Exception ex)
    {
        var message = new StringBuilder();
        var current = ex;

        while (current != null)
        {
            if (message.Length > 0)
                message.Append(" | Inner: ");

            message.Append(current.GetType().Name);
            message.Append(": ");
            message.Append(current.Message);
            current = current.InnerException;
        }

        return message.ToString();
    }
}
