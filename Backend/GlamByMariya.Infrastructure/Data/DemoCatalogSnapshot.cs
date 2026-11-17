using System.Text.Json;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

// Single-process Demo catalog snapshot. Media pointers use persisted item GUIDs.
public sealed class DemoCatalogSnapshot : IDisposable
{
    private sealed record State(List<CatalogItem> Services, List<CatalogItem> AddOns);
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream lease;
    private State? state;

    public DemoCatalogSnapshot(string root)
    {
        Directory.CreateDirectory(root);
        lease = new FileStream(Path.Combine(root, "catalog.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        path = Path.Combine(root, "catalog.json");
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Demo catalog file is too large.");
            state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid Demo catalog.");
            Validate(state);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { lease.Dispose(); throw new InvalidDataException("Demo catalog cannot be safely loaded; existing files were not changed.", e); }
        catch { lease.Dispose(); throw; }
    }

    private static CatalogItem Copy(CatalogItem item) => new()
    {
        Id = item.Id, Name = item.Name, Description = item.Description, Price = item.Price,
        GroupPrice = item.GroupPrice, GroupMinimumPeople = item.GroupMinimumPeople,
        Minutes = item.Minutes, Active = item.Active, Archived = item.Archived,
        Category = item.Category, VisualWord = item.VisualWord
    };
    private static State Copy(State source) => new(source.Services.Select(Copy).ToList(), source.AddOns.Select(Copy).ToList());
    private static void Validate(State source)
    {
        if (source.Services == null || source.AddOns == null || source.Services.Count + source.AddOns.Count > 5000)
            throw new InvalidDataException("Invalid Demo catalog lists.");
        var ids = new HashSet<Guid>();
        foreach (var (item, service) in source.Services.Select(x => (x, true)).Concat(source.AddOns.Select(x => (x, false))))
        {
            if (item == null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 ||
                item.Description == null || item.Description.Length > 10000 || item.Category == null || item.Category.Length > 60 || item.VisualWord == null || item.VisualWord.Length > 40 ||
                item.Price < 0 || item.Price > 1_000_000 || Money.Round(item.Price) != item.Price || item.Minutes < 0 || item.Minutes > 480 ||
                (service && (item.Price <= 0 || item.Minutes <= 0 || item.GroupMinimumPeople < 2 || item.GroupMinimumPeople > BookingPricing.MaximumPeople ||
                    (item.GroupPrice.HasValue && (item.GroupPrice <= 0 || item.GroupPrice > item.Price || Money.Round(item.GroupPrice.Value) != item.GroupPrice)))) ||
                (!service && item.GroupPrice.HasValue))
                throw new InvalidDataException("Invalid Demo catalog item.");
        }
    }
    private void Commit(State next)
    {
        Validate(next);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, next); output.Flush(true); }
        File.Move(temporary, path, true);
        state = next;
    }

    public (List<CatalogItem> Services, List<CatalogItem> AddOns) LoadOrInitialize(IEnumerable<CatalogItem> services, IEnumerable<CatalogItem> addOns)
    {
        lock (gate)
        {
            if (state == null)
            {
                // A nonempty root could contain media from a lost catalog. Never
                // silently give those files newly generated identities.
                if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Any(x => Path.GetFileName(x) != "catalog.lock"))
                    throw new InvalidDataException("Demo catalog is missing in a nonempty folder; no seed was created.");
                Commit(new State(services.Select(Copy).ToList(), addOns.Select(Copy).ToList()));
            }
            var snapshot = Copy(state!); return (snapshot.Services, snapshot.AddOns);
        }
    }
    public void Save(IEnumerable<CatalogItem> services, IEnumerable<CatalogItem> addOns)
    {
        lock (gate)
        {
            if (state == null) throw new InvalidOperationException("Initialize the Demo catalog first.");
            Commit(new State(services.Select(Copy).ToList(), addOns.Select(Copy).ToList()));
        }
    }
    public void Dispose() => lease.Dispose();
}
