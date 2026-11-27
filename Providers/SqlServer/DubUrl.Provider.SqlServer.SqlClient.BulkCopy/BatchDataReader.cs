using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;

namespace DubUrl.Provider.SqlServer.SqlClient.BulkCopy;

/// <summary>Exposes a batch without letting SqlClient read ahead into the next one.</summary>
internal sealed class BatchDataReader(IDataReader source, int batchSize) : DbDataReader
{
    private int rowsRemaining;
    private bool firstRowPending;
    private bool sourceExhausted;

    public bool StartBatch()
    {
        if (sourceExhausted || !source.Read())
        {
            sourceExhausted = true;
            return false;
        }

        firstRowPending = true;
        rowsRemaining = batchSize;
        return true;
    }

    public override bool Read()
    {
        if (rowsRemaining == 0 || sourceExhausted)
            return false;

        if (!firstRowPending && !source.Read())
        {
            sourceExhausted = true;
            return false;
        }

        firstRowPending = false;
        rowsRemaining--;
        return true;
    }

    public override bool NextResult() => false;
    public override int Depth => source.Depth;
    public override int FieldCount => source.FieldCount;
    public override bool HasRows => true;
    public override bool IsClosed => source.IsClosed;
    public override int RecordsAffected => source.RecordsAffected;
    public override object this[int ordinal] => source[ordinal];
    public override object this[string name] => source[name];
    public override bool GetBoolean(int ordinal) => source.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => source.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => source.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => source.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => source.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => source.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => source.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => source.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => source.GetDouble(ordinal);
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);
    public override Type GetFieldType(int ordinal) => source.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => source.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => source.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => source.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => source.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => source.GetInt64(ordinal);
    public override string GetName(int ordinal) => source.GetName(ordinal);
    public override int GetOrdinal(string name) => source.GetOrdinal(name);
    public override DataTable? GetSchemaTable() => source.GetSchemaTable();
    public override string GetString(int ordinal) => source.GetString(ordinal);
    public override object GetValue(int ordinal)
        => source is SqlDataReader reader && reader.GetProviderSpecificFieldType(ordinal) == typeof(SqlDecimal)
            ? reader.GetSqlDecimal(ordinal)
            : source.GetValue(ordinal);
    public override int GetValues(object[] values) => source.GetValues(values);
    public override bool IsDBNull(int ordinal) => source.IsDBNull(ordinal);
    public override Stream GetStream(int ordinal)
        => source is DbDataReader reader ? reader.GetStream(ordinal) : base.GetStream(ordinal);
    public override TextReader GetTextReader(int ordinal)
        => source is DbDataReader reader ? reader.GetTextReader(ordinal) : base.GetTextReader(ordinal);
}
