using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HermesProxy;

public class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        var commandTree = new RootCommand("Hermes Proxy: Allows you to play on legacy WoW server with modern client")
        {
            CommandLineArgumentsTemplate.ConfigFileLocation,
            CommandLineArgumentsTemplate.DisableVersionCheck,
            CommandLineArgumentsTemplate.OverwrittenConfigValues,
        };

        commandTree.SetAction(result =>
        {
            var commandLineArguments = new CommandLineArguments
            {
                ConfigFileLocation = result.GetValue(CommandLineArgumentsTemplate.ConfigFileLocation),
                DisableVersionCheck = result.GetValue(CommandLineArgumentsTemplate.DisableVersionCheck),
                OverwrittenConfigValues = ParseMultiArgument(result.GetValue(CommandLineArgumentsTemplate.OverwrittenConfigValues)),
            };
            Server.ServerMain(commandLineArguments);
        });

        int exitCode = 1;
        try
        {
             exitCode = commandTree.Parse(args).Invoke();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error occured: {e}");
        }

        if (OsSpecific.AreWeInOurOwnConsole())
        {
            // If we would exit immediately the console would close and the user cannot read the error
            // The delay is there if for some reason STDIN is already closed
            Thread.Sleep(TimeSpan.FromSeconds(3));

            Console.WriteLine("Press enter to close");
            Console.ReadLine();
        }

        return exitCode;
    }

    private static Dictionary<string, string> ParseMultiArgument(string[]? multiArgs)
    {
        if (multiArgs == null)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var arg in multiArgs)
        {
            var keyValue = arg.Split('=', 2);
            if (keyValue.Length != 2)
                throw new Exception($"Invalid argument '{arg}'");
            result[keyValue[0]] = keyValue[1];
        }
        return result;
    }

    public static class CommandLineArgumentsTemplate
    {
        public static readonly Option<string?> ConfigFileLocation = new("--config")
        {
            Description = "The config file that will be used",
            DefaultValueFactory = _ => "HermesProxy.config",
            CustomParser = result =>
            {
                string? filePath = result.Tokens.Single().Value;
                if (!File.Exists(filePath))
                {
                    result.AddError($"Error: config file '{filePath}' does not exist");
                    return null;
                }

                return filePath;
            }
        };

        public static readonly Option<bool> DisableVersionCheck = new("--no-version-check")
        {
            Description = "Disables the initial version update check"
        };

        public static readonly Option<string[]> OverwrittenConfigValues = new("--set")
        {
            Description = "Overwrites a specific config value. Example: --set ServerAddress=logon.example.com"
        };
    }
}

public class CommandLineArguments
{
    public string? ConfigFileLocation { init; get; }
    public bool DisableVersionCheck { init; get; }
    public Dictionary<string, string> OverwrittenConfigValues { init; get; }
}

internal static class OsSpecific
{
    /// Checks whenever or not we are in our own console
    /// For example on Windows you can just double click the exe which spawns a new Console Window Host
    public static bool AreWeInOurOwnConsole()
    {
        try
        {
#if _WINDOWS
            var consoleWindowHandle = GetConsoleWindow();
            GetWindowThreadProcessId(consoleWindowHandle, out var consoleWindowProcess);
            var weAreTheOwner = (consoleWindowProcess == Environment.ProcessId);
            return weAreTheOwner;
#else
            return true;
#endif
        }
        catch
        {
            return false;
        }
    }

#if _WINDOWS
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError=true)]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
#endif
}
