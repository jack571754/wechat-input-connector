namespace Connector.Windows;

public static class ApplicationInstance
{
    public static bool Run(string name, Action startup)
    {
        using var mutex = new Mutex(false, name);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            startup(); return true;
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }
}
