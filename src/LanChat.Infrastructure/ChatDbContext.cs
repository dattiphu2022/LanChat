using System.Reflection;
using LanChat.Domain;
using Microsoft.Data.Sqlite;

namespace LanChat.Infrastructure;

public sealed class ChatDatabase
{
    public static readonly IReadOnlyList<string> MigrationIds = Array.AsReadOnly(new[]
    {
        "20260921071741_InitialPhase1", "20260921072743_LocalReadState", "20260921081251_DistributedPhases",
        "20261113093000_LocalConversationVisibility", "20261116100000_PeerAddresses", "20261116130000_EndToEndEncryption",
        "20261117150000_PendingPairGroups", "20261118100000_GroupHistoryKeys", "20261119090000_MessageMentions"
    });
    private readonly string _connectionString;
    public ChatDatabase(string dataDirectory)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "lanchat.db"),
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
    }

    public ChatDbContext Open(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return new ChatDbContext(connection, ct); }
        catch { connection.Dispose(); throw; }
    }

    public void Migrate(string? through = null, CancellationToken ct = default)
    {
        using var db = Open(ct);
        db.Execute("PRAGMA journal_mode=WAL;");
        db.Execute("PRAGMA secure_delete=ON;");
        db.Begin();
        db.Execute("CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);");
        using (var command = db.Command("SELECT MigrationId FROM __EFMigrationsHistory"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (!MigrationIds.Contains(reader.GetString(0)))
                    throw new InvalidDataException("Cơ sở dữ liệu thuộc phiên bản LanChat mới hơn.");
        foreach (var id in MigrationIds)
        {
            if (db.Scalar("SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId=$0", id) == 0)
            {
                using var resource = typeof(ChatDatabase).Assembly.GetManifestResourceStream("LanChat.Infrastructure.Migrations." + id + ".sql")
                    ?? throw new InvalidOperationException("Thiếu migration " + id);
                using var reader = new StreamReader(resource);
                db.Execute(reader.ReadToEnd());
                db.Execute("INSERT INTO __EFMigrationsHistory VALUES ($0, $1)", id, "9.0.15");
            }
            if (id == through) break;
        }
        db.Commit();
    }
}

public sealed class ChatDbContext : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CancellationToken _ct;
    private SqliteTransaction? _transaction;
    private readonly Dictionary<(Type Type, Guid Id), (object Row, object?[] Values)> _tracked = new();
    internal bool ContentChanged { get; private set; }
    internal bool PresenceChanged { get; private set; }
    internal ChatDbContext(SqliteConnection connection, CancellationToken ct) { _connection = connection; _ct = ct; }
    internal void Begin() => _transaction = _connection.BeginTransaction();
    internal void Commit() { SaveChanges(); _ct.ThrowIfCancellationRequested(); _transaction!.Commit(); }
    public void Dispose() { _transaction?.Dispose(); _connection.Dispose(); }

    internal SqliteCommand Command(string sql, params object?[] args)
    {
        _ct.ThrowIfCancellationRequested();
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        for (var j = 0; j < args.Length; j++) command.Parameters.AddWithValue("$" + j, Encode(args[j]));
        return command;
    }
    public int Execute(string sql, params object?[] args)
    {
        using var command = Command(sql, args);
        return command.ExecuteNonQuery();
    }
    internal void NoteContentChange() => ContentChanged = true;
    internal void NotePresenceChange() => PresenceChanged = true;
    public int ExecuteInterpolated(FormattableString sql) =>
        Execute(string.Format(sql.Format, Enumerable.Range(0, sql.ArgumentCount).Select(j => (object)("$" + j)).ToArray()), sql.GetArguments());
    public string TextScalar(string sql)
    {
        using var command = Command(sql);
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
    public long Scalar(string sql, params object?[] args)
    {
        using var command = Command(sql, args);
        return Convert.ToInt64(command.ExecuteScalar());
    }
    internal long Count<T>(string where = "1", params object?[] args) =>
        Scalar($"SELECT COUNT(*) FROM {Table<T>.Name} WHERE {where}", args);
    internal static string Parameters(int count) => string.Join(",", Enumerable.Range(0, count).Select(j => "$" + j));
    internal bool Exists<T>(string where = "1", params object?[] args) =>
        Scalar($"SELECT EXISTS(SELECT 1 FROM {Table<T>.Name} WHERE {where})", args) != 0;
    internal T? One<T>(string where = "1", params object?[] args) where T : class, new() =>
        Rows<T>(where, args).SingleOrDefault();
    internal T Required<T>(string where = "1", params object?[] args) where T : class, new() =>
        Rows<T>(where, args).Single();
    internal T[] Rows<T>(string where = "1", params object?[] args) where T : class, new() =>
        Query<T>($"SELECT {Table<T>.Columns} FROM {Table<T>.Name} WHERE {where}", args);

    internal T[] Query<T>(string sql, params object?[] args) where T : class, new()
    {
        using var command = Command(sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        var properties = Table<T>.Properties;
        var ordinals = properties.Select(p => reader.GetOrdinal(p.Name)).ToArray();
        while (reader.Read())
        {
            _ct.ThrowIfCancellationRequested();
            var row = new T();
            var values = new object?[properties.Length];
            for (var j = 0; j < properties.Length; j++)
            {
                var index = ordinals[j];
                if (!reader.IsDBNull(index)) values[j] = Decode(reader.GetValue(index), properties[j].PropertyType);
                properties[j].SetValue(row, values[j]);
            }
            var key = (typeof(T), (Guid)Table<T>.Id.GetValue(row)!);
            if (_transaction is null) rows.Add(row);
            else if (_tracked.TryGetValue(key, out var tracked)) rows.Add((T)tracked.Row);
            else { _tracked.Add(key, (row, values)); rows.Add(row); }
        }
        return rows.ToArray();
    }

    public void Insert<T>(T row) where T : class, new()
    {
        var values = Table<T>.Properties.Select(p => p.GetValue(row)).ToArray();
        Execute($"INSERT INTO {Table<T>.Name} ({Table<T>.Columns}) VALUES ({Parameters(values.Length)})", values);
        if (typeof(T) != typeof(ClusterState) && typeof(T) != typeof(ControlLogEntry) && typeof(T) != typeof(PeerNode)) ContentChanged = true;
        if (_transaction is not null) _tracked.Add((typeof(T), (Guid)Table<T>.Id.GetValue(row)!), (row, values));
    }

    internal int Delete<T>(string where, params object?[] args) where T : class, new()
    {
        var removed = Rows<T>(where, args);
        var count = Execute($"DELETE FROM {Table<T>.Name} WHERE {where}", args);
        if (count != 0 && typeof(T) != typeof(ControlLogEntry)) ContentChanged = true;
        foreach (var row in removed) _tracked.Remove((typeof(T), (Guid)Table<T>.Id.GetValue(row)!));
        return count;
    }

    internal void SaveChanges()
    {
        foreach (var entry in _tracked)
        {
            var mapping = Tables[entry.Key.Type];
            var (row, previous) = entry.Value;
            var columns = new List<string>();
            var values = new List<object?>();
            for (var j = 0; j < mapping.Properties.Length; j++)
            {
                var property = mapping.Properties[j];
                var value = property.GetValue(row);
                if (Equals(value, previous[j])) continue;
                columns.Add("\"" + property.Name + "\"=$" + values.Count);
                values.Add(value);
                previous[j] = value;
            }
            if (columns.Count == 0) continue;
            if (entry.Key.Type != typeof(ClusterState) && (entry.Key.Type != typeof(PeerNode) ||
                columns.Any(c => c.StartsWith("\"IPAddress\"", StringComparison.Ordinal) || c.StartsWith("\"ServicePort\"", StringComparison.Ordinal))))
                ContentChanged = true;
            var idParameter = "$" + values.Count;
            values.Add(entry.Key.Id);
            Execute($"UPDATE {mapping.Name} SET {string.Join(",", columns)} WHERE Id={idParameter}", values.ToArray());
        }
    }

    private static object Encode(object? value) => value switch
    {
        null => DBNull.Value, Guid id => id.ToString().ToUpperInvariant(), Enum e => Convert.ToInt32(e), _ => value
    };
    private static object Decode(object value, Type property)
    {
        var type = Nullable.GetUnderlyingType(property) ?? property;
        if (type == typeof(Guid)) return Guid.Parse((string)value);
        if (type.IsEnum) return Enum.ToObject(type, value);
        return Convert.ChangeType(value, type);
    }

    private static readonly Dictionary<Type, (string Name, PropertyInfo[] Properties)> Tables = new()
    {
        [typeof(LocalUserProfile)] = Map<LocalUserProfile>("Users"),
        [typeof(PeerNode)] = Map<PeerNode>("PeerNodes"),
        [typeof(Conversation)] = Map<Conversation>("Conversations"),
        [typeof(ConversationMember)] = Map<ConversationMember>("ConversationMembers"),
        [typeof(Message)] = Map<Message>("Messages"),
        [typeof(MessageMentionProjection)] = Map<MessageMentionProjection>("MessageMentions"),
        [typeof(LocalSettings)] = Map<LocalSettings>("LocalSettings"),
        [typeof(ReplicationEvent)] = Map<ReplicationEvent>("ReplicationEvents"),
        [typeof(ReplicationCursor)] = Map<ReplicationCursor>("ReplicationCursors"),
        [typeof(OutboxItem)] = Map<OutboxItem>("OutboxItems"),
        [typeof(Group)] = Map<Group>("Groups"),
        [typeof(GroupMember)] = Map<GroupMember>("GroupMembers"),
        [typeof(MessageReaction)] = Map<MessageReaction>("MessageReactions"),
        [typeof(MessageReceipt)] = Map<MessageReceipt>("MessageReceipts"),
        [typeof(Attachment)] = Map<Attachment>("Attachments"),
        [typeof(ClusterState)] = Map<ClusterState>("ClusterState"),
        [typeof(ControlLogEntry)] = Map<ControlLogEntry>("ControlLog")
    };
    private static (string, PropertyInfo[]) Map<T>(string table) => (table, typeof(T).GetProperties());
    private static class Table<T>
    {
        private static readonly (string Name, PropertyInfo[] Properties) Mapping = Tables[typeof(T)];
        public static readonly string Name = Mapping.Name;
        public static readonly PropertyInfo[] Properties = Mapping.Properties;
        public static readonly PropertyInfo Id = typeof(T).GetProperty("Id")!;
        public static readonly string Columns = string.Join(",", Properties.Select(p => "\"" + p.Name + "\""));
    }
}
