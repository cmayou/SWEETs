using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using DataSheetMfgPartsImport.Interop;

namespace DataSheetMfgPartsImport.Services;

/// <summary>
/// Fallback reader for circuit types straight from the SOLIDWORKS Electrical application database:
/// <code>
///   [tew_app_data].[tew].[tew_element]         ele_id, ele_keycode
///   [tew_app_data].[tew].[tew_translatedtext]  tra_objectid = ele_id, language + text columns
/// </code>
/// Read-only. The connection comes from IEwEnvironmentX (connection string, or host / user / password).
/// The language and text column names of tew_translatedtext are detected by name, so minor schema
/// differences between versions don't break it.
/// </summary>
internal static class CircuitTypeDatabase
{
    private const string Database = "tew_app_data";

    public static void Fill(IEwEnvironmentX env, List<CircuitTypeInfo> types, string language)
    {
        var ids = types.Where(t => t.Id > 0).Select(t => t.Id).Distinct().ToList();
        if (ids.Count == 0) return;

        using var connection = new SqlConnection(ConnectionString(env));
        connection.Open();
        var idList = string.Join(",", ids);   // integers only, safe to inline

        // ---- key codes
        var keyCodes = new Dictionary<int, string>();
        using (var cmd = new SqlCommand($"SELECT ele_id, ele_keycode FROM [{Database}].[tew].[tew_element] WHERE ele_id IN ({idList})", connection))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (!r.IsDBNull(1)) keyCodes[Convert.ToInt32(r.GetValue(0))] = Convert.ToString(r.GetValue(1)).Trim();

        // ---- translated descriptions (column names detected)
        var texts = new Dictionary<int, List<(string lang, string text)>>();
        using (var cmd = new SqlCommand($"SELECT * FROM [{Database}].[tew].[tew_translatedtext] WHERE tra_objectid IN ({idList})", connection))
        using (var r = cmd.ExecuteReader())
        {
            var schema = Enumerable.Range(0, r.FieldCount).Select(i => (i, name: r.GetName(i), type: r.GetFieldType(i))).ToList();
            var objCol = schema.First(c => c.name.Equals("tra_objectid", StringComparison.OrdinalIgnoreCase)).i;
            var langCol = schema.Where(c => c.type == typeof(string) && (c.name.IndexOf("lang", StringComparison.OrdinalIgnoreCase) >= 0 || c.name.IndexOf("lng", StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(c => (int?)c.i).FirstOrDefault();
            var textCol = schema.Where(c => c.type == typeof(string) && c.i != langCol &&
                    (c.name.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0 || c.name.IndexOf("value", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     c.name.IndexOf("desc", StringComparison.OrdinalIgnoreCase) >= 0 || c.name.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(c => (int?)c.i).FirstOrDefault()
                ?? schema.Where(c => c.type == typeof(string) && c.i != langCol).Select(c => (int?)c.i).FirstOrDefault();
            Log.Info($"tew_translatedtext columns: {string.Join(", ", schema.Select(c => c.name))}; language = {(langCol.HasValue ? schema[langCol.Value].name : "-")}, text = {(textCol.HasValue ? schema[textCol.Value].name : "-")}");
            if (textCol == null) return;

            while (r.Read())
            {
                if (r.IsDBNull(objCol) || r.IsDBNull(textCol.Value)) continue;
                var id = Convert.ToInt32(r.GetValue(objCol));
                var lang = langCol.HasValue && !r.IsDBNull(langCol.Value) ? r.GetString(langCol.Value).Trim() : "";
                if (!texts.TryGetValue(id, out var list)) texts[id] = list = new List<(string, string)>();
                list.Add((lang, r.GetString(textCol.Value).Trim()));
            }
        }

        var filled = 0;
        foreach (var t in types)
        {
            if (string.IsNullOrWhiteSpace(t.KeyCode) && keyCodes.TryGetValue(t.Id, out var key)) { t.KeyCode = key; filled++; }
            if (string.IsNullOrWhiteSpace(t.Description) && texts.TryGetValue(t.Id, out var list) && list.Count > 0)
            {
                var pick = list.FirstOrDefault(x => string.Equals(x.lang, language, StringComparison.OrdinalIgnoreCase))
                           .text
                           ?? list.FirstOrDefault(x => x.lang.StartsWith(language ?? "en", StringComparison.OrdinalIgnoreCase)).text
                           ?? list.FirstOrDefault(x => x.lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)).text
                           ?? list[0].text;
                if (!string.IsNullOrWhiteSpace(pick)) { t.Description = pick; filled++; }
            }
        }
        Log.Info($"Circuit types from database: {keyCodes.Count} key code(s), {texts.Count} description set(s), {filled} value(s) filled.");
    }

    private static string ConnectionString(IEwEnvironmentX env)
    {
        var raw = env.getDatabaseConnectionString(out var e);
        if (Com.Ok(e) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var b = new SqlConnectionStringBuilder(raw);
                if (string.IsNullOrEmpty(b.InitialCatalog)) b.InitialCatalog = Database;
                b.ConnectTimeout = Math.Min(b.ConnectTimeout, 10);
                return b.ConnectionString;
            }
            catch (Exception ex)
            {
                Log.Warn($"Connection string from Electrical is not SqlClient format ({ex.Message}); using host / user / password.");
            }
        }

        var host = env.getHostname(out _);
        var user = env.getUserID(out _);
        var password = env.getPassword(out _);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = host,
            InitialCatalog = Database,
            ConnectTimeout = 10,
        };
        if (string.IsNullOrEmpty(user)) builder.IntegratedSecurity = true;
        else { builder.UserID = user; builder.Password = password; }
        return builder.ConnectionString;
    }
}
