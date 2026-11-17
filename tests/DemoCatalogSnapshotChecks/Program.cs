using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;

void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "demo-catalog-check-" + Guid.NewGuid().ToString("N"));
var seed = new DemoStore();
Guid service, addon, created;
using (var disk = new DemoCatalogSnapshot(root))
{
    var first = disk.LoadOrInitialize(seed.Services, seed.AddOns);
    service = first.Services[0].Id; addon = first.AddOns[0].Id;
    first.Services[0].Name = "Synthetic renamed service";
    first.Services[0].Price = 700; first.Services[0].GroupPrice = 500;
    first.AddOns[0].Name = "Synthetic updated addon";
    var added = new CatalogItem { Name = "Synthetic new service", Price = 900, Minutes = 45 };
    created = added.Id; first.Services.Add(added);
    disk.Save(first.Services, first.AddOns);
    first.Services[0].Name = "Uncommitted mutation";
    Check(disk.LoadOrInitialize([], []).Services[0].Name == "Synthetic renamed service", "returned records are independent copies");
    try { using var second = new DemoCatalogSnapshot(root); throw new Exception("second writer accepted"); }
    catch (IOException) { Console.WriteLine("PASS exclusive folder lease"); }
}
using (var disk = new DemoCatalogSnapshot(root))
{
    var restored = disk.LoadOrInitialize([], []);
    Check(restored.Services[0].Id == service && restored.AddOns[0].Id == addon && restored.Services.Any(x => x.Id == created), "stable IDs across reopen");
    Check(restored.Services[0].Name == "Synthetic renamed service" && restored.Services[0].Price == 700 && restored.Services[0].GroupPrice == 500 && restored.AddOns[0].Name == "Synthetic updated addon", "catalog edits and pricing survive reopen");
    restored.AddOns[0].Id = service;
    try { disk.Save(restored.Services, restored.AddOns); throw new Exception("duplicate identity accepted"); }
    catch (InvalidDataException) { Console.WriteLine("PASS duplicate identities rejected before write"); }
    Check(disk.LoadOrInitialize([], []).AddOns[0].Id == addon, "invalid save does not replace state");
}
var missing = root + "-missing"; Directory.CreateDirectory(missing); File.WriteAllText(Path.Combine(missing, "existing-media.png"), "synthetic");
try { using var disk = new DemoCatalogSnapshot(missing); disk.LoadOrInitialize(seed.Services, seed.AddOns); throw new Exception("missing catalog reseeded"); }
catch (InvalidDataException) { Check(File.Exists(Path.Combine(missing, "existing-media.png")) && !File.Exists(Path.Combine(missing, "catalog.json")), "missing catalog with existing files fails closed"); }
var corrupt = root + "-corrupt"; Directory.CreateDirectory(corrupt); var corruptPath = Path.Combine(corrupt, "catalog.json"); File.WriteAllText(corruptPath, "{broken");
try { using var disk = new DemoCatalogSnapshot(corrupt); throw new Exception("corrupt catalog accepted"); }
catch (InvalidDataException) { Check(File.ReadAllText(corruptPath) == "{broken", "corrupt catalog is preserved and rejected"); }
Console.WriteLine("Retained synthetic test folders: " + root);
