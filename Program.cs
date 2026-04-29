using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ──────────────────────────────────────────────────────────────
// Storage: SQLite embedded. Path configurable via LUCA_DB env var.
// Default: ./data/registry.db (Render creates working dir).
// ──────────────────────────────────────────────────────────────
var dbPath = Environment.GetEnvironmentVariable("LUCA_DB")
             ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "registry.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connString = $"Data Source={dbPath}";

// Initialize schema
using (var conn = new SqliteConnection(connString))
{
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS hosts (
            machine_id TEXT PRIMARY KEY,
            tunnel_url TEXT NOT NULL,
            local_ip TEXT,
            zerotier_ip TEXT,
            version TEXT,
            estudio_nombre TEXT,
            registered_at TEXT NOT NULL DEFAULT (datetime('now')),
            last_seen TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX IF NOT EXISTS idx_hosts_last_seen ON hosts(last_seen);

        CREATE TABLE IF NOT EXISTS invitation_codes (
            code             TEXT PRIMARY KEY,
            machine_id       TEXT NOT NULL,
            expires_at       TEXT NOT NULL,
            estudio_nombre   TEXT,
            role             TEXT,
            registered_at    TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX IF NOT EXISTS idx_codes_machine ON invitation_codes(machine_id);
        CREATE INDEX IF NOT EXISTS idx_codes_expires ON invitation_codes(expires_at);
    """;
    cmd.ExecuteNonQuery();
}

// ──────────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────────
static string NormalizeMachineId(string id) => id.Trim().ToUpperInvariant();

static string? NormalizeCode(string? code)
{
    if (string.IsNullOrWhiteSpace(code)) return null;
    var normalized = code.Trim().ToUpperInvariant();
    return Regex.IsMatch(normalized, "^INV-[A-Z0-9]{4}-[A-Z0-9]{4}$") ? normalized : null;
}

static bool IsOnline(DateTime lastSeenUtc) =>
    DateTime.UtcNow - lastSeenUtc < TimeSpan.FromMinutes(15);

// ──────────────────────────────────────────────────────────────
// Endpoints
// ──────────────────────────────────────────────────────────────

app.MapGet("/", () => Results.Text(
    """
    Luca Registry — central registry for Luca on-premise instances.
    Endpoints:
      POST   /registry/register
      POST   /registry/heartbeat
      GET    /registry/locate/{machineId}
      GET    /registry/list
      POST   /codes
      GET    /codes/{code}
      DELETE /codes/{code}
      GET    /health
    """, "text/plain"));

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// POST /registry/register — first-time registration
app.MapPost("/registry/register", async (RegisterRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId) || string.IsNullOrWhiteSpace(req.TunnelUrl))
        return Results.BadRequest(new { error = "machineId and tunnelUrl are required" });

    var id = NormalizeMachineId(req.MachineId);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO hosts (machine_id, tunnel_url, local_ip, zerotier_ip, version, estudio_nombre, registered_at, last_seen)
        VALUES ($id, $url, $local, $zt, $ver, $nom, datetime('now'), datetime('now'))
        ON CONFLICT(machine_id) DO UPDATE SET
            tunnel_url = excluded.tunnel_url,
            local_ip = excluded.local_ip,
            zerotier_ip = excluded.zerotier_ip,
            version = excluded.version,
            estudio_nombre = excluded.estudio_nombre,
            last_seen = datetime('now');
    """;
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$url", req.TunnelUrl.Trim());
    cmd.Parameters.AddWithValue("$local", (object?)req.LocalIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$zt", (object?)req.ZerotierIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$ver", (object?)req.Version ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$nom", (object?)req.EstudioNombre ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { machineId = id, registered = true });
});

// POST /registry/heartbeat — keep-alive + update location
app.MapPost("/registry/heartbeat", async (HeartbeatRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId) || string.IsNullOrWhiteSpace(req.TunnelUrl))
        return Results.BadRequest(new { error = "machineId and tunnelUrl are required" });

    var id = NormalizeMachineId(req.MachineId);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        UPDATE hosts SET
            tunnel_url = $url,
            local_ip = COALESCE($local, local_ip),
            zerotier_ip = COALESCE($zt, zerotier_ip),
            version = COALESCE($ver, version),
            estudio_nombre = COALESCE($nom, estudio_nombre),
            last_seen = datetime('now')
        WHERE machine_id = $id;
    """;
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$url", req.TunnelUrl.Trim());
    cmd.Parameters.AddWithValue("$local", (object?)req.LocalIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$zt", (object?)req.ZerotierIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$ver", (object?)req.Version ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$nom", (object?)req.EstudioNombre ?? DBNull.Value);
    var rows = await cmd.ExecuteNonQueryAsync();

    if (rows == 0)
        return Results.NotFound(new { error = "host not registered — call /registry/register first" });

    return Results.Ok(new { machineId = id, heartbeat = DateTime.UtcNow });
});

// GET /registry/locate/{machineId} — find a host
app.MapGet("/registry/locate/{machineId}", async (string machineId) =>
{
    var id = NormalizeMachineId(machineId);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT machine_id, tunnel_url, local_ip, zerotier_ip, version, estudio_nombre, registered_at, last_seen
        FROM hosts WHERE machine_id = $id;
    """;
    cmd.Parameters.AddWithValue("$id", id);
    using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Results.NotFound(new { error = "machine not found" });

    var lastSeen = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(7)), DateTimeKind.Utc);
    var registeredAt = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(6)), DateTimeKind.Utc);

    return Results.Ok(new LocateResponse(
        MachineId: reader.GetString(0),
        TunnelUrl: reader.GetString(1),
        LocalIp: reader.IsDBNull(2) ? null : reader.GetString(2),
        ZerotierIp: reader.IsDBNull(3) ? null : reader.GetString(3),
        Version: reader.IsDBNull(4) ? null : reader.GetString(4),
        EstudioNombre: reader.IsDBNull(5) ? null : reader.GetString(5),
        RegisteredAt: registeredAt,
        LastSeen: lastSeen,
        Status: IsOnline(lastSeen) ? "online" : "offline"));
});

// GET /r/{machineId}/{code} — stable invite link.
// 302 redirect a {currentTunnelUrl}/join/{code}. Esto hace que los links de
// invitación NUNCA mueran: el host es el registry estable, no el tunnel
// efímero del owner. El owner puede rotar tunnel 100 veces y el link sigue
// llevando al lugar correcto porque la resolución es just-in-time.
app.MapGet("/r/{machineId}/{code}", async (string machineId, string code) =>
{
    var id = NormalizeMachineId(machineId);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT tunnel_url, last_seen FROM hosts WHERE machine_id = $id;";
    cmd.Parameters.AddWithValue("$id", id);
    using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Results.Content(
            $"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Luca — Estudio offline</title>" +
            $"<style>body{{font-family:system-ui;max-width:480px;margin:60px auto;padding:24px;background:#0a0a0a;color:#e5e5e5;text-align:center}}h1{{font-size:22px}}</style></head>" +
            $"<body><h1>Estudio no encontrado</h1><p>Este link de invitación no corresponde a ningún estudio registrado. Pedile al OWNER una invitación nueva.</p></body></html>",
            "text/html; charset=utf-8", System.Text.Encoding.UTF8);

    var tunnelUrl = reader.GetString(0).TrimEnd('/');
    var lastSeen = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(1)), DateTimeKind.Utc);

    if (!IsOnline(lastSeen))
        return Results.Content(
            $"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Luca — Estudio offline</title>" +
            $"<style>body{{font-family:system-ui;max-width:480px;margin:60px auto;padding:24px;background:#0a0a0a;color:#e5e5e5;text-align:center}}h1{{font-size:22px}}.muted{{color:#888;font-size:14px;margin-top:16px}}</style></head>" +
            $"<body><h1>El estudio está offline</h1><p>El servidor del estudio no está prendido en este momento. Pedile al OWNER que abra Luca y reintenta el link.</p>" +
            $"<p class='muted'>Última conexión: {lastSeen:O}</p></body></html>",
            "text/html; charset=utf-8", System.Text.Encoding.UTF8);

    var target = $"{tunnelUrl}/join/{code}?m={id}";
    return Results.Redirect(target, permanent: false);
});

// GET /j/{code} — smart invite link.
// PUBLIC, sin auth. El owner manda este URL por WhatsApp/email. El invitado
// hace click → este HTML se carga, intenta abrir luca:// (deep-link a la app
// si está instalada), y si falla en 1.5s redirige al download. Bonus: muestra
// nombre del estudio + rol antes de abrir nada.
app.MapGet("/j/{code}", async (string code) =>
{
    var normalizedCode = NormalizeCode(code);
    if (normalizedCode is null)
        return Results.Content(InviteHtml.Invalid("Código de invitación inválido."),
            "text/html; charset=utf-8", System.Text.Encoding.UTF8);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT c.machine_id, c.expires_at, c.estudio_nombre, c.role, h.last_seen, h.tunnel_url
        FROM invitation_codes c
        LEFT JOIN hosts h ON h.machine_id = c.machine_id
        WHERE c.code = $code;
    """;
    cmd.Parameters.AddWithValue("$code", normalizedCode);
    using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Results.Content(InviteHtml.Invalid(
            "Esta invitación no existe o ya fue eliminada."),
            "text/html; charset=utf-8", System.Text.Encoding.UTF8);

    var expiresAt = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(1)), DateTimeKind.Utc);
    if (expiresAt < DateTime.UtcNow)
        return Results.Content(InviteHtml.Invalid("Esta invitación expiró."),
            "text/html; charset=utf-8", System.Text.Encoding.UTF8);

    var estudioNombre = reader.IsDBNull(2) ? "Estudio Luca" : reader.GetString(2);
    var role = reader.IsDBNull(3) ? "miembro" : reader.GetString(3);
    var ownerOnline = !reader.IsDBNull(4) &&
        IsOnline(DateTime.SpecifyKind(DateTime.Parse(reader.GetString(4)), DateTimeKind.Utc));
    var tunnelUrl = reader.IsDBNull(5) ? null : reader.GetString(5).TrimEnd('/');

    return Results.Content(
        InviteHtml.SmartLink(normalizedCode, estudioNombre, role, ownerOnline, tunnelUrl),
        "text/html; charset=utf-8", System.Text.Encoding.UTF8);
});

// GET /registry/list — admin/debug view (returns all hosts)
app.MapGet("/registry/list", async () =>
{
    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT machine_id, tunnel_url, version, estudio_nombre, registered_at, last_seen
        FROM hosts ORDER BY last_seen DESC;
    """;
    using var reader = await cmd.ExecuteReaderAsync();

    var hosts = new List<object>();
    while (await reader.ReadAsync())
    {
        var lastSeen = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(5)), DateTimeKind.Utc);
        hosts.Add(new
        {
            machineId = reader.GetString(0),
            tunnelUrl = reader.GetString(1),
            version = reader.IsDBNull(2) ? null : reader.GetString(2),
            estudioNombre = reader.IsDBNull(3) ? null : reader.GetString(3),
            registeredAt = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(4)), DateTimeKind.Utc),
            lastSeen,
            status = IsOnline(lastSeen) ? "online" : "offline"
        });
    }
    return Results.Ok(hosts);
});

// ──────────────────────────────────────────────────────────────
// Invitation codes — owner publishes a code → invitee resolves to estudio
// ──────────────────────────────────────────────────────────────

// POST /codes — owner publishes (or refreshes) an invitation code
app.MapPost("/codes", async (CodeRegisterRequest req) =>
{
    var code = NormalizeCode(req.Code);
    if (code is null)
        return Results.BadRequest(new { error = "invalid_code_format" });

    if (string.IsNullOrWhiteSpace(req.MachineId))
        return Results.BadRequest(new { error = "machineId is required" });

    var id = NormalizeMachineId(req.MachineId);

    // expiresAt: parse as UTC, store as ISO-8601 ('o') for stable round-tripping
    if (!DateTime.TryParse(req.ExpiresAt, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var expiresAt))
        return Results.BadRequest(new { error = "invalid_expires_at" });
    expiresAt = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();

    // Check host exists
    var checkHost = conn.CreateCommand();
    checkHost.CommandText = "SELECT 1 FROM hosts WHERE machine_id = $id;";
    checkHost.Parameters.AddWithValue("$id", id);
    var hostExists = await checkHost.ExecuteScalarAsync();
    if (hostExists is null)
        return Results.NotFound(new { error = "host_not_registered" });

    // Check if code already owned by a different machine
    var checkOwner = conn.CreateCommand();
    checkOwner.CommandText = "SELECT machine_id FROM invitation_codes WHERE code = $code;";
    checkOwner.Parameters.AddWithValue("$code", code);
    var existingOwner = await checkOwner.ExecuteScalarAsync() as string;
    if (existingOwner is not null && existingOwner != id)
        return Results.Conflict(new { error = "code_owned_by_other_machine" });

    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO invitation_codes (code, machine_id, expires_at, estudio_nombre, role, registered_at)
        VALUES ($code, $id, $exp, $nom, $role, datetime('now'))
        ON CONFLICT(code) DO UPDATE SET
            machine_id = excluded.machine_id,
            expires_at = excluded.expires_at,
            estudio_nombre = excluded.estudio_nombre,
            role = excluded.role;
    """;
    cmd.Parameters.AddWithValue("$code", code);
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$exp", expiresAt.ToString("o"));
    cmd.Parameters.AddWithValue("$nom", (object?)req.EstudioNombre ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$role", (object?)req.Role ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { code, registered = true });
});

// GET /codes/{code} — invitee resolves a bare code to estudio + tunnel URL
app.MapGet("/codes/{code}", async (string code) =>
{
    var c = NormalizeCode(code);
    if (c is null)
        return Results.BadRequest(new { error = "invalid_code_format" });

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT ic.code, ic.machine_id, ic.expires_at, ic.estudio_nombre, ic.role,
               h.tunnel_url, h.last_seen
        FROM invitation_codes ic
        LEFT JOIN hosts h ON h.machine_id = ic.machine_id
        WHERE ic.code = $code;
    """;
    cmd.Parameters.AddWithValue("$code", c);
    using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Results.NotFound(new { error = "code_not_found" });

    var expiresAt = DateTime.Parse(reader.GetString(2), null,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    if (expiresAt < DateTime.UtcNow)
    {
        reader.Close();
        var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM invitation_codes WHERE code = $code;";
        del.Parameters.AddWithValue("$code", c);
        await del.ExecuteNonQueryAsync();
        return Results.Json(new { error = "code_expired" }, statusCode: 410);
    }

    var machineId = reader.GetString(1);
    var estudioNombre = reader.IsDBNull(3) ? null : reader.GetString(3);
    var role = reader.IsDBNull(4) ? null : reader.GetString(4);
    var tunnelUrl = reader.IsDBNull(5) ? null : reader.GetString(5);
    var ownerStatus = "offline";
    if (!reader.IsDBNull(6))
    {
        var lastSeen = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(6)), DateTimeKind.Utc);
        ownerStatus = IsOnline(lastSeen) ? "online" : "offline";
    }

    return Results.Ok(new CodeLookupResponse(
        Code: c,
        MachineId: machineId,
        TunnelUrl: tunnelUrl,
        EstudioNombre: estudioNombre,
        Role: role,
        OwnerStatus: ownerStatus,
        ExpiresAt: expiresAt));
});

// DELETE /codes/{code} — owner unregisters a code (revoked / expired)
app.MapDelete("/codes/{code}", async (string code) =>
{
    var c = NormalizeCode(code);
    if (c is null)
        return Results.BadRequest(new { error = "invalid_code_format" });

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM invitation_codes WHERE code = $code;";
    cmd.Parameters.AddWithValue("$code", c);
    var rows = await cmd.ExecuteNonQueryAsync();

    return rows == 0 ? Results.NotFound(new { error = "code_not_found" }) : Results.NoContent();
});

// ──────────────────────────────────────────────────────────────
// Run — Render provides PORT env var
// ──────────────────────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");


// ──────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────
public record RegisterRequest(
    string MachineId,
    string TunnelUrl,
    string? LocalIp = null,
    string? ZerotierIp = null,
    string? Version = null,
    string? EstudioNombre = null);

public record HeartbeatRequest(
    string MachineId,
    string TunnelUrl,
    string? LocalIp = null,
    string? ZerotierIp = null,
    string? Version = null,
    string? EstudioNombre = null);

public record LocateResponse(
    string MachineId,
    string TunnelUrl,
    string? LocalIp,
    string? ZerotierIp,
    string? Version,
    string? EstudioNombre,
    DateTime RegisteredAt,
    DateTime LastSeen,
    string Status);

public record CodeRegisterRequest(
    string Code,
    string MachineId,
    string ExpiresAt,
    string? EstudioNombre = null,
    string? Role = null);

public record CodeLookupResponse(
    string Code,
    string MachineId,
    string? TunnelUrl,
    string? EstudioNombre,
    string? Role,
    string OwnerStatus,
    DateTime ExpiresAt);

// HTML helpers para /j/{code} smart-link page.
public static class InviteHtml
{
    public static string SmartLink(string code, string estudioNombre, string role, bool ownerOnline, string? tunnelUrl)
    {
        var safeCode    = System.Net.WebUtility.HtmlEncode(code);
        var safeEstudio = System.Net.WebUtility.HtmlEncode(estudioNombre);
        var safeRole    = System.Net.WebUtility.HtmlEncode(role);
        var statusBadge = ownerOnline
            ? "<span style='color:#86efac'>● online</span>"
            : "<span style='color:#f59e0b'>● offline (el owner no está conectado ahora)</span>";

        // Downloads se sirven desde el tunnel del owner ({tunnelUrl}/downloads/*).
        // El MSI/tar.gz lo bakea cada owner local (mismo binario para todos
        // pero hosteado por cada studio). Si el owner está offline, los
        // downloads van a fallar — se muestra warning correspondiente.
        var msiHref = !string.IsNullOrEmpty(tunnelUrl) && ownerOnline
            ? $"{tunnelUrl}/downloads/luca-setup.msi"
            : "#";
        var macHref = !string.IsNullOrEmpty(tunnelUrl) && ownerOnline
            ? $"{tunnelUrl}/downloads/luca-mac-arm64.tar.gz"
            : "#";
        var downloadsDisabled = !ownerOnline
            ? "<p class='muted' style='color:#f59e0b'>El owner está offline — pedile que abra Luca y volvé a clickear el link.</p>"
            : "";

        return $@"<!DOCTYPE html>
<html lang=""es""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Invitación a {safeEstudio} — Luca</title>
<style>
*{{box-sizing:border-box}}
body{{font-family:system-ui,-apple-system,Segoe UI,sans-serif;max-width:520px;margin:60px auto;padding:24px;background:#0a0a0a;color:#e5e5e5;line-height:1.5}}
h1{{font-size:14px;font-weight:500;color:#888;margin:0 0 6px;text-transform:uppercase;letter-spacing:1px}}
h2{{font-size:28px;font-weight:600;margin:0 0 18px}}
.role{{font-size:14px;color:#aaa;margin-bottom:24px}}
.code-box{{background:#1a1a1a;padding:14px 16px;border-radius:8px;font-family:'Cascadia Code',Consolas,monospace;font-size:18px;text-align:center;letter-spacing:2px;border:1px solid #2a2a2a;font-weight:600;margin:20px 0}}
.btn{{display:block;background:#3b82f6;color:#fff;padding:14px 22px;border-radius:8px;font-weight:600;font-size:15px;text-decoration:none;text-align:center;border:none;cursor:pointer;width:100%;margin-top:12px}}
.btn:hover{{background:#2563eb}}
.btn-secondary{{background:#27272a}}
.btn-secondary:hover{{background:#3a3a3a}}
.btn[href='#']{{opacity:0.4;pointer-events:none}}
.muted{{color:#888;font-size:13px;margin-top:18px}}
.status{{font-size:13px;margin-bottom:20px}}
.foot{{margin-top:32px;padding-top:16px;border-top:1px solid #1a1a1a;color:#666;font-size:12px}}
</style></head><body>

<h1>Invitación a estudio Luca</h1>
<h2>{safeEstudio}</h2>
<div class=""role"">Rol: <strong>{safeRole}</strong></div>
<div class=""status"">Owner: {statusBadge}</div>

<p>Para entrar tenés 2 caminos según si ya tenés Luca instalado:</p>

<div style=""margin-top:20px"">
  <h3 style=""font-size:15px;color:#aaa;margin:0 0 10px;text-transform:uppercase;letter-spacing:1px"">Si ya tenés Luca</h3>
  <p class=""muted"">Abrí Luca, andá a <strong>""Tengo un código de invitación""</strong> y pegá:</p>
  <div class=""code-box"">{safeCode}</div>
</div>

<div style=""margin-top:30px"">
  <h3 style=""font-size:15px;color:#aaa;margin:0 0 10px;text-transform:uppercase;letter-spacing:1px"">Si no tenés Luca</h3>
  <p class=""muted"">Bajalo desde el servidor del estudio (el OWNER lo está alojando):</p>
  <a class=""btn"" href=""{msiHref}"" download>Descargar Luca para Windows</a>
  <a class=""btn btn-secondary"" href=""{macHref}"" download>Descargar Luca para Mac (Apple Silicon)</a>
  {downloadsDisabled}
  <p class=""muted"">Después de instalar, abrí la app y pegá el código que ves arriba.</p>
</div>

<div class=""foot"">Luca — plataforma para estudios contables. Tu data vive en tu máquina, no en la nube.</div>
</body></html>";
    }

    public static string Invalid(string mensaje)
    {
        var safe = System.Net.WebUtility.HtmlEncode(mensaje);
        return $@"<!DOCTYPE html>
<html lang=""es""><head><meta charset=""utf-8""><title>Invitación inválida — Luca</title>
<style>body{{font-family:system-ui;max-width:480px;margin:80px auto;padding:24px;background:#0a0a0a;color:#e5e5e5;text-align:center}}h1{{font-size:22px}}.muted{{color:#888;font-size:14px;margin-top:18px}}</style>
</head><body>
<h1>Invitación inválida</h1>
<p>{safe}</p>
<p class=""muted"">Pedile al OWNER del estudio una invitación nueva.</p>
</body></html>";
    }
}
