using AkashicRecords.Hub;

// Akashic sync hub.
//   AkashicRecords.Hub                 run the server (console, Windows service or systemd)
//   AkashicRecords.Hub pair            print a one-time pairing code
//   AkashicRecords.Hub devices         list paired devices
//   AkashicRecords.Hub revoke <id>     revoke a device's token
//   AkashicRecords.Hub config          print the effective settings
// Settings: see HubOptions (hub.json, hub.local.json, AKASHIC_HUB_* variables, --Hub:Key=value).

var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : null;
if (command is null)
{
    HubApp.Build(args).Run();
    return 0;
}

try
{
    var rest = args.Skip(1).ToArray();
    var options = HubOptions.Load(rest.Where(a => a.StartsWith("--")).ToArray());

    if (command == "config")
    {
        Console.WriteLine($"Adresse   : {options.EffectiveUrl}");
        Console.WriteLine($"Donnees   : {options.DataDir}");
        Console.WriteLine($"Retention : {options.RetentionDays} jours");
        Console.WriteLine($"Types     : {string.Join(", ", options.EntityTypes)}");
        return 0;
    }

    Directory.CreateDirectory(options.DataDir);
    var store = new HubStore(options.DatabasePath); // same SQLite file as the running server

    switch (command)
    {
        case "pair":
            var code = store.CreatePairCode(options.PairCodeMinutes);
            Console.WriteLine($"Code d'appairage : {code[..4]} {code[4..]}");
            Console.WriteLine($"Valable {options.PairCodeMinutes} minutes, pour un seul appareil.");
            return 0;

        case "devices":
            var devices = store.Devices();
            if (devices.Count == 0) Console.WriteLine("Aucun appareil appaire.");
            foreach (var d in devices)
                Console.WriteLine($"{d.DeviceId}  {d.Name}  appaire {d.CreatedAt[..10]}  vu {d.LastSeenAt?[..16] ?? "jamais"}{(d.Revoked ? "  [REVOQUE]" : "")}");
            return 0;

        case "revoke":
            var id = rest.FirstOrDefault(a => !a.StartsWith("--"));
            if (id is null) { Console.Error.WriteLine("Usage : AkashicRecords.Hub revoke <device_id>"); return 2; }
            var done = store.Revoke(id);
            Console.WriteLine(done ? $"Appareil '{id}' revoque : son jeton ne marche plus." : $"Aucun appareil actif '{id}'.");
            return done ? 0 : 1;

        default:
            Console.Error.WriteLine($"Commande inconnue : {command}. Commandes : pair, devices, revoke <id>, config.");
            return 2;
    }
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"Impossible d'ouvrir les donnees du hub : {ex.Message}");
    Console.Error.WriteLine("Lance cette commande avec les memes droits que le service (Windows : PowerShell administrateur ; Linux : sudo -u akashic).");
    return 3;
}
