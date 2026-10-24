using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using System.Diagnostics;
using System.Reflection;

namespace Skua.Core.Plugins;

public delegate void PluginLoadDelegate(IPluginContainer container);

public class PluginManager : IPluginManager
{
    private Dictionary<ISkuaPlugin, IPluginContainer> _plugins = new();

    public List<IPluginContainer> Containers => _plugins.Values.ToList();

    public void Initialize()
    {
        // Plugins built against another Skua version ask for that version of
        // Skua's assemblies; .NET refuses a newer one than is loaded. Give them
        // ours: a plugin that only uses what exists here then works, and one
        // that does not fails on the missing member, with a clear message.
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
            name.Name is { } n && n.StartsWith("Skua.", StringComparison.Ordinal)
                ? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == n)
                : null;

        Directory.CreateDirectory(Path.Combine(ClientFileSources.SkuaPluginsDIR, "options"));
        Directory.GetFiles(ClientFileSources.SkuaPluginsDIR)
            .Where(f => f.EndsWith(".dll"))
            .ForEach(f =>
            {
                Exception? e = Load(f);
                // To the log, not only Trace: a plugin that fails to load
                // otherwise just does not show up.
                if (e is not null)
                    Console.Error.WriteLine($"[plugins] {Path.GetFileName(f)} not loaded: {Describe(e)}");
                else
                    Console.Error.WriteLine($"[plugins] {Path.GetFileName(f)} loaded");
            });
    }

    /// <summary>
    /// A load failure in a line: the innermost cause, and for a missing member
    /// or assembly a hint, as these are plugins built for another Skua.
    /// </summary>
    public static string Describe(Exception e)
    {
        Exception inner = e;
        while (inner.InnerException is not null)
            inner = inner.InnerException;
        if (inner is ReflectionTypeLoadException types && types.LoaderExceptions.FirstOrDefault(x => x is not null) is { } first)
            inner = first;
        string message = inner.Message;
        if (inner is MissingMethodException or MissingFieldException or TypeLoadException or FileNotFoundException or FileLoadException)
            message += " (the plugin was built for another Skua version or client)";
        return message;
    }

    public Exception? Load(string path)
    {
        ISkuaPlugin? plugin = null;

        try
        {
            Assembly asm = Assembly.LoadFrom(path);
            TypeInfo? type = asm.DefinedTypes.FirstOrDefault(t => typeof(ISkuaPlugin).IsAssignableFrom(t));
            if (type is null)
                return new Exception("Plugin class not found.");

            if (Activator.CreateInstance(type) is not ISkuaPlugin pluginInstance)
                return new Exception("Failed to create plugin instance.");

            plugin = pluginInstance;
            PluginContainer? container = new(plugin);

            // Add to dictionary first to ensure proper tracking
            _plugins.Add(plugin, container);

            try
            {
                plugin.Load(Ioc.Default, Ioc.Default.GetRequiredService<IPluginHelper>());
                container.OptionContainer.SetDefaults();
                container.OptionContainer.Load();
                StrongReferenceMessenger.Default.Send<PluginLoadedMessage, int>(new(container), (int)MessageChannels.Plugins);
                return null;
            }
            catch (Exception loadException)
            {
                // If plugin.Load() fails, remove from dictionary to prevent partial state
                _plugins.Remove(plugin);

                // Attempt to unload the plugin gracefully
                try
                {
                    plugin.Unload();
                }
                catch
                {
                    // Ignore unload exceptions since loading already failed
                }

                throw new Exception($"Failed to initialize plugin: {loadException.Message}", loadException);
            }
        }
        catch (Exception e)
        {
            // Ensure cleanup if we have references but something went wrong
            if (plugin != null)
            {
                _plugins.Remove(plugin);
            }

            return e;
        }
    }

    /// <summary>
    /// Unloads the given plugin.
    /// </summary>
    /// <param name="plugin">The plugin to unload.</param>
    public void Unload(ISkuaPlugin plugin)
    {
        StrongReferenceMessenger.Default.Send<PluginUnloadedMessage, int>(new(_plugins[plugin]), (int)MessageChannels.Plugins);
        plugin.Unload();
        _plugins.Remove(plugin);
    }

    /// <summary>
    /// Unloads the plugin by it's given name.
    /// </summary>
    /// <param name="pluginName">The name o the plugin to unload.</param>
    public void Unload(string pluginName)
    {
        ISkuaPlugin? plugin = Containers.Find(x => x.ToString() == pluginName)?.Plugin;
        if (plugin is null)
            return;

        StrongReferenceMessenger.Default.Send<PluginUnloadedMessage, int>(new(_plugins[plugin]), (int)MessageChannels.Plugins);
        plugin.Unload();
        _plugins.Remove(plugin);
    }

    /// <summary>
    /// Gets the container for the given plugin.
    /// </summary>
    /// <param name="plugin">The plugin to get the container for.</param>
    /// <returns>The plugin's container.</returns>
    public IPluginContainer? GetContainer(ISkuaPlugin plugin)
    {
        return _plugins.TryGetValue(plugin, out IPluginContainer? container) ? container : null;
    }

    /// <summary>
    /// Gets the container for the given plugin name.
    /// </summary>
    /// <param name="pluginName">Name of the plugin to get the container for.</param>
    /// <returns>The plugin's container</returns>
    public IPluginContainer? GetContainer(string pluginName)
    {
        return Containers.Exists(x => x.ToString() == pluginName) ? Containers.Find(x => x.ToString() == pluginName) : null;
    }

    /// <summary>
    /// Gets the container for the plugin with the given type.
    /// </summary>
    /// <typeparam name="T">The type of the plugin to get the container for.</typeparam>
    /// <returns>The container for the plugin with the given type.</returns>
    public IPluginContainer GetContainer<T>() where T : ISkuaPlugin
    {
        return _plugins.FirstOrDefault(kvp => kvp.Key is T).Value;
    }
}