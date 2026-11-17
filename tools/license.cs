// Makes and signs Workbench license keys. Run with .NET 10:  dotnet run tools/license.cs -- <command>
//
//   keygen <folder>                                  one time: writes private.pem (keep it secret and backed up) and public.pem
//   issue2 <private.pem> <licensee> <seats> <yyyy-mm-dd> [--modules finance,capture,...|all] [--light <n>] [--trial] [--id <guid>]
//                                                    prints a WB2 license key (modules, light seats, trial); core is always included
//   issue <private.pem> <licensee> <seats> <yyyy-mm-dd> [business]    prints a WB1 key (every module, per-person seats; kept for older customers)
//
// Put the contents of public.pem in LicenseOptions.DefaultPublicKeyPem (or the License:PublicKeyPem setting) on the servers.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 0) { Console.Error.WriteLine("usage: keygen <folder> | issue2 <private.pem> <licensee> <seats> <yyyy-mm-dd> [--modules a,b|all] [--light n] [--trial] [--id guid] | issue <private.pem> <licensee> <seats> <yyyy-mm-dd> [business]"); return 2; }

static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

switch (args[0])
{
    case "keygen":
    {
        var dir = Directory.CreateDirectory(args[1]).FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = Path.Combine(dir, "private.pem");
        if (File.Exists(priv)) { Console.Error.WriteLine($"{priv} already exists; not overwriting."); return 1; }
        File.WriteAllText(priv, key.ExportPkcs8PrivateKeyPem());
        File.SetUnixFileMode(priv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(Path.Combine(dir, "public.pem"), key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
        return 0;
    }
    case "issue":
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(args[1]));
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", 1);
            json.WriteString("licensee", args[2]);
            json.WriteString("edition", args.Length > 5 ? args[5] : "business");
            json.WriteNumber("seats", int.Parse(args[3]));
            json.WriteString("expires", args[4]);
            json.WriteString("issued", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            json.WriteEndObject();
        }

        var payload = buffer.ToArray();
        var sig = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine($"WB1.{B64(payload)}.{B64(sig)}");
        return 0;
    }
    case "issue2":
    {
        if (args.Length < 5) { Console.Error.WriteLine("usage: issue2 <private.pem> <licensee> <seats> <yyyy-mm-dd> [--modules a,b|all] [--light n] [--trial] [--id guid]"); return 2; }
        string[] known = ["finance", "capture", "workforce", "controls", "govcon", "enterprise"];
        var modules = new List<string>();
        var light = 0;
        var trial = false;
        var id = Guid.NewGuid();
        for (var i = 5; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--modules":
                    var list = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => m.ToLowerInvariant()).ToList();
                    modules = list.Contains("all") ? [.. known] : list;
                    break;
                case "--light": light = int.Parse(args[++i]); break;
                case "--trial": trial = true; break;
                case "--id": id = Guid.Parse(args[++i]); break;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }

        var unknown = modules.Where(m => !known.Contains(m) && m != "core").ToList();
        if (unknown.Count > 0) { Console.Error.WriteLine($"unknown module(s): {string.Join(", ", unknown)}. Known: core (implied), {string.Join(", ", known)}"); return 2; }
        if (!DateOnly.TryParseExact(args[4], "yyyy-MM-dd", out _)) { Console.Error.WriteLine("expiry must be yyyy-mm-dd"); return 2; }

        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(args[1]));
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", 2);
            json.WriteString("licenseId", id.ToString());
            json.WriteString("licensee", args[2]);
            json.WriteString("issuedAt", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            json.WriteString("expires", args[4]);
            json.WriteNumber("seats", int.Parse(args[3]));
            json.WriteNumber("lightSeats", light);
            json.WriteStartArray("modules");
            foreach (var m in modules.Where(m => m != "core").Distinct()) json.WriteStringValue(m);
            json.WriteEndArray();
            json.WriteBoolean("trial", trial);
            json.WriteEndObject();
        }

        var payload = buffer.ToArray();
        var sig = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine($"WB2.{B64(payload)}.{B64(sig)}");
        return 0;
    }
    default:
        Console.Error.WriteLine("unknown command");
        return 2;
}
