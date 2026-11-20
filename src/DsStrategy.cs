using System;
using System.Collections.Generic;
using System.IO;

internal struct StrategySelection<T>
{
    public readonly T BuiltIn;
    public readonly DsStrategy Imported;

    public StrategySelection(T builtIn)
    {
        BuiltIn = builtIn;
        Imported = null;
    }

    public StrategySelection(DsStrategy imported)
    {
        BuiltIn = default(T);
        Imported = imported;
    }

    public override string ToString()
    {
        return Imported == null ? BuiltIn.ToString() : "DS:" + Imported.Name;
    }
}

internal sealed class DsStrategy
{
    private static readonly IList<DsStrategy> All = Load().AsReadOnly();

    public readonly string Name, Web, Quic, Google, IpQuic, Voice, Media;
    public readonly string Description, YouTubeDescription;

    private DsStrategy(string name, Dictionary<string, string> options)
    {
        Name = name;
        Description = options["Description"];
        string youtubeDescription;
        YouTubeDescription = options.TryGetValue("YouTubeDescription", out youtubeDescription) ? youtubeDescription : Description;
        Web = options["Web"];
        Quic = options["Quic"];
        Google = options["Google"];
        IpQuic = options["IpQuic"];
        Voice = options["Voice"];
        Media = options["Media"];
    }

    public static DsStrategy Get(string name)
    {
        foreach (var strategy in All)
            if (strategy.Name == name) return strategy;
        throw new ArgumentException("Неизвестная стратегия ds.", "name");
    }

    private static List<DsStrategy> Load()
    {
        var strategies = new List<DsStrategy>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string name = null;
        using (var reader = new StreamReader(typeof(DsStrategy).Assembly.GetManifestResourceStream("ds-strategies.args")))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]", StringComparison.Ordinal))
                {
                    if (name != null) strategies.Add(new DsStrategy(name, options));
                    name = line.Substring(1, line.Length - 2);
                    options.Clear();
                    continue;
                }
                var separator = line.IndexOf('=');
                if (name == null || separator < 1)
                    throw new InvalidDataException("Некорректная строка в каталоге стратегий ds.");
                options.Add(line.Substring(0, separator), line.Substring(separator + 1));
            }
        }
        if (name != null) strategies.Add(new DsStrategy(name, options));
        return strategies;
    }
}
