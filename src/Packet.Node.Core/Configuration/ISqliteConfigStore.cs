namespace Packet.Node.Core.Configuration;

/// <summary>
/// The thin persistence seam behind <see cref="SqliteConfigProvider"/>: the singleton
/// <c>node_config</c> row in <c>pdn.db</c>. The store knows ONLY SQL + the canonical
/// JSON (de)serialisation; the provider owns the validate / atomic-apply / OnChange
/// logic. This split mirrors how the supervisor and the other stores are kept apart.
/// </summary>
public interface ISqliteConfigStore
{
    /// <summary>
    /// Load the singleton config row. Returns <c>null</c> when the row is absent (a
    /// fresh DB, or one that was wiped) — the provider's first-boot migration/seed path
    /// keys off exactly this. On a present row, returns the deserialised
    /// <see cref="NodeConfig"/> together with the <c>schema_ver</c> carried alongside it
    /// (so a future loader can migrate older blobs). A store/parse fault is surfaced as
    /// <c>null</c> too (degrade-safe — the provider then re-seeds), having logged.
    /// </summary>
    (NodeConfig Config, int SchemaVer)? Load();

    /// <summary>
    /// Upsert the singleton config row (<c>id = 1</c>) with the canonical JSON blob of
    /// <paramref name="config"/>, its <see cref="NodeConfig.SchemaVersion"/>, the
    /// <c>format</c> discriminator (<c>json</c>) and an ISO-8601 update stamp. Returns
    /// <c>true</c> on a successful persist, <c>false</c> if the write faulted (logged) —
    /// the provider treats a <c>false</c> as "do not advance Current".
    /// </summary>
    bool Save(NodeConfig config);
}
