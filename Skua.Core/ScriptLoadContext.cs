using Skua.Core.Models;
using System.Reflection;
using System.Runtime.Loader;

namespace Skua.Core;

public class ScriptLoadContext : AssemblyLoadContext
{
    private static readonly string _cacheDirectory = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Cached-Scripts");
    private volatile bool _isUnloading;

    public ScriptLoadContext() : base(isCollectible: true)
    {
        Unloading += context => _isUnloading = true;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == null)
            return null;

        // The dynamic binder (Microsoft.CSharp) keeps every type it has bound
        // against in one process-wide table, so a script's types, and with
        // them its whole context, never unloaded: every run kept its compiled
        // script and CoreBots in memory. With its own copy of the binder the
        // table lives in this context and goes with it.
        if (assemblyName.Name == "Microsoft.CSharp")
        {
            try
            {
                return LoadFromAssemblyPath(typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly.Location);
            }
            catch
            {
            }
        }

        if (!Directory.Exists(_cacheDirectory))
            return null;

        if (_isUnloading)
            return null;

        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch
        {
        }

        if (_isUnloading)
            return null;

        string[] matchingFiles = Directory.GetFiles(_cacheDirectory, $"*-{assemblyName.Name}.dll");

        if (matchingFiles.Length > 0)
        {
            string latestFile = matchingFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();

            if (!File.Exists(latestFile))
                return null;

            try
            {
                using FileStream stream = File.OpenRead(latestFile);
                return LoadFromStream(stream);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}