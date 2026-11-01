// Makes and signs Workbench license keys. Run with .NET 10:  dotnet run tools/license.cs -- <command>
//
//   keygen <folder>                                  one time: writes private.pem (keep it secret and backed up) and public.pem
//   issue <private.pem> <licensee> <seats> <yyyy-mm-dd> [business]    prints a license key to give the customer
//
// Put the contents of public.pem in LicenseOptions.DefaultPublicKeyPem (or the License:PublicKeyPem setting) on the servers.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 0) { Console.Error.WriteLine("usage: keygen <folder> | issue <private.pem> <licensee> <seats> <yyyy-mm-dd> [business]"); return 2; }

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
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            licensee = args[2],
            edition = args.Length > 5 ? args[5] : "business",
            seats = int.Parse(args[3]),
            expires = args[4],
            issued = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        });
        var sig = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine($"WB1.{B64(payload)}.{B64(sig)}");
        return 0;
    }
    default:
        Console.Error.WriteLine("unknown command");
        return 2;
}
