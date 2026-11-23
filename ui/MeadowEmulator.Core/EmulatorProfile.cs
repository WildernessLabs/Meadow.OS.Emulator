using YamlDotNet.RepresentationModel;

namespace MeadowEmulator.Core;

public enum OsVariant { Legacy, Rebase }

public enum Coprocessor { Stub, DualMachine, EspEmu }

/// <summary>
/// One emulator configuration from configs/*.yaml. OS and board together are a
/// build identity, because the board is baked into the generated hooks.
/// Defaults and validation follow scripts/read-config.sh.
/// </summary>
public sealed record EmulatorProfile(
    string ConfigPath,
    OsVariant Os,
    string Board,
    Coprocessor Coprocessor,
    int MonitorPort,
    int GdbPort)
{
    private static readonly string[] ValidBoards = ["F7FeatherV1", "F7FeatherV2", "F7CoreComputeV2"];

    public string Name => Path.GetFileNameWithoutExtension(ConfigPath);

    /// <summary>LittleFS major version this OS mounts.</summary>
    public int LfsMajor => Os == OsVariant.Legacy ? 1 : 2;

    /// <summary>
    /// USART1 log. The .resc scripts hard-code this path and ignore
    /// debug.console_log, so it is derived from the OS and not read from the config.
    /// </summary>
    public string SyslogPath => Os == OsVariant.Legacy
        ? "/tmp/renode-uart-legacy.txt"
        : "/tmp/renode-uart-meadow.txt";

    public string BuildDirectory(string repoRoot) =>
        Os == OsVariant.Legacy ? Path.Combine(repoRoot, "build", "legacy") : Path.Combine(repoRoot, "build");

    public static EmulatorProfile Load(string path)
    {
        var stream = new YamlStream();
        using (var reader = File.OpenText(path))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"{path}: not a YAML mapping.");
        }

        var os = Value(root, "os", "legacy") switch
        {
            "legacy" => OsVariant.Legacy,
            "meadow-os" => OsVariant.Rebase,
            var other => throw new InvalidDataException($"{path}: invalid os '{other}' (valid: legacy, meadow-os)."),
        };

        var board = Value(root, "board", "F7FeatherV2");
        if (!ValidBoards.Contains(board))
        {
            throw new InvalidDataException($"{path}: invalid board '{board}' (valid: {string.Join(", ", ValidBoards)}).");
        }

        var coprocessor = Value(root, "coprocessor", "stub") switch
        {
            "stub" => Coprocessor.Stub,
            "dual-machine" => Coprocessor.DualMachine,
            "esp-emu" => Coprocessor.EspEmu,
            var other => throw new InvalidDataException($"{path}: invalid coprocessor '{other}' (valid: stub, dual-machine, esp-emu)."),
        };

        return new EmulatorProfile(
            Path.GetFullPath(path),
            os,
            board,
            coprocessor,
            Port(root, path, "ports.monitor", "1234", allowZero: false),
            Port(root, path, "ports.gdb", "0", allowZero: true));
    }

    /// <summary>Loads every valid profile in configs/, Rebase first.</summary>
    public static IReadOnlyList<EmulatorProfile> LoadAll(string repoRoot) =>
        Directory.EnumerateFiles(Path.Combine(repoRoot, "configs"), "*.yaml")
            .Select(Load)
            .OrderBy(p => p.Os == OsVariant.Rebase ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    private static int Port(YamlMappingNode root, string path, string key, string fallback, bool allowZero)
    {
        var text = Value(root, key, fallback);
        if (!int.TryParse(text, out var port) || port > 65535 || port < (allowZero ? 0 : 1))
        {
            throw new InvalidDataException($"{path}: invalid {key} '{text}'.");
        }

        return port;
    }

    private static string Value(YamlMappingNode root, string dottedKey, string fallback)
    {
        YamlNode node = root;
        foreach (var key in dottedKey.Split('.'))
        {
            if (node is not YamlMappingNode map || !map.Children.TryGetValue(new YamlScalarNode(key), out var next))
            {
                return fallback;
            }

            node = next;
        }

        return node is YamlScalarNode { Value: { Length: > 0 } value } ? value : fallback;
    }
}
