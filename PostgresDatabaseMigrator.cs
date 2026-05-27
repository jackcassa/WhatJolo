using System.Data.Common;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace WhatJolo;

internal sealed class PostgresDatabaseMigrator
{
    private readonly string _postgresConnectionString;
    private readonly string _sqliteConnectionString;

    public PostgresDatabaseMigrator(string postgresConnectionString, string sqliteConnectionString)
    {
        _postgresConnectionString = postgresConnectionString;
        _sqliteConnectionString = sqliteConnectionString;
    }

    public void EnsureReady()
    {
        using var postgresConnection = new NpgsqlConnection(_postgresConnectionString);
        postgresConnection.Open();
        EnsureSchema(postgresConnection);

        var sqliteBuilder = new SqliteConnectionStringBuilder(_sqliteConnectionString);
        if (string.IsNullOrWhiteSpace(sqliteBuilder.DataSource) || !File.Exists(sqliteBuilder.DataSource))
        {
            return;
        }

        using var sqliteConnection = new SqliteConnection(_sqliteConnectionString);
        sqliteConnection.Open();
        MigrateData(sqliteConnection, postgresConnection);
    }

    private static void EnsureSchema(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS cropasset
            (
                id BIGSERIAL PRIMARY KEY,
                sourceimagepath TEXT NOT NULL,
                cropimagepath TEXT NOT NULL,
                crophash TEXT NOT NULL UNIQUE,
                x INTEGER NOT NULL,
                y INTEGER NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_cropasset_cropimagepath
                ON cropasset(cropimagepath);

            CREATE TABLE IF NOT EXISTS projectcroplink
            (
                id BIGSERIAL PRIMARY KEY,
                projectname TEXT NOT NULL,
                labelname TEXT NOT NULL,
                cropassetid BIGINT NOT NULL REFERENCES cropasset(id) ON DELETE CASCADE,
                isvariation INTEGER NOT NULL DEFAULT 0,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(projectname, labelname, cropassetid)
            );

            CREATE INDEX IF NOT EXISTS ix_projectcroplink_projectname
                ON projectcroplink(projectname, labelname, updatedatutc DESC);

            CREATE TABLE IF NOT EXISTS contacts
            (
                id BIGSERIAL PRIMARY KEY,
                phonenumber TEXT NOT NULL,
                phonenumbernormalized TEXT NOT NULL DEFAULT '',
                firstname TEXT NOT NULL,
                lastname TEXT NOT NULL,
                chat INTEGER NOT NULL DEFAULT 0,
                test INTEGER NOT NULL DEFAULT 0,
                sent INTEGER NOT NULL DEFAULT 0,
                exclude INTEGER NOT NULL DEFAULT 0,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_contacts_phonenumber
                ON contacts(phonenumber);

            CREATE UNIQUE INDEX IF NOT EXISTS ix_contacts_phonenumbernormalized
                ON contacts(phonenumbernormalized);

            CREATE TABLE IF NOT EXISTS incomingmessages
            (
                id BIGSERIAL PRIMARY KEY,
                phonenumber TEXT NOT NULL,
                messagetimestamputc TEXT NOT NULL,
                whatsappmessageid TEXT NOT NULL DEFAULT '',
                messagetype TEXT NOT NULL DEFAULT '',
                messageack TEXT NOT NULL DEFAULT '',
                isfromme INTEGER NOT NULL DEFAULT 0,
                messagetext TEXT NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS ix_incomingmessages_phonenumber_timestamp
                ON incomingmessages(phonenumber, messagetimestamputc DESC);

            CREATE INDEX IF NOT EXISTS ix_incomingmessages_whatsappmessageid
                ON incomingmessages(whatsappmessageid);

            CREATE TABLE IF NOT EXISTS messagenotifications
            (
                id BIGSERIAL PRIMARY KEY,
                incomingmessageid BIGINT NOT NULL REFERENCES incomingmessages(id) ON DELETE CASCADE,
                phonenumber TEXT NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                processed INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_messagenotifications_processed_id
                ON messagenotifications(processed, id);

            CREATE TABLE IF NOT EXISTS projectactiveclass
            (
                id BIGSERIAL PRIMARY KEY,
                projectname TEXT NOT NULL,
                classname TEXT NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(projectname, classname)
            );

            CREATE INDEX IF NOT EXISTS ix_projectactiveclass_projectname
                ON projectactiveclass(projectname, classname);

            CREATE TABLE IF NOT EXISTS projectinfo
            (
                id BIGSERIAL PRIMARY KEY,
                projectname TEXT NOT NULL UNIQUE,
                projectrootpath TEXT NOT NULL,
                machinename TEXT NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS ix_projectinfo_projectname
                ON projectinfo(projectname);
            """;
        command.ExecuteNonQuery();
    }

    private void MigrateData(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        CopyCropAsset(sqliteConnection, postgresConnection);
        CopyProjectCropLink(sqliteConnection, postgresConnection);
        CopyContacts(sqliteConnection, postgresConnection);
        CopyIncomingMessages(sqliteConnection, postgresConnection);
        CopyMessageNotifications(sqliteConnection, postgresConnection);
        CopyProjectActiveClass(sqliteConnection, postgresConnection);
        CopyProjectInfo(sqliteConnection, postgresConnection);
        ResetSequences(postgresConnection);
    }

    private static void CopyCropAsset(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        using var selectCommand = sqliteConnection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT Id, SourceImagePath, CropImagePath, CropHash, X, Y, Width, Height, CreatedAtUtc, UpdatedAtUtc
            FROM CropAsset;
            """;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            using var insertCommand = postgresConnection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO cropasset
                (
                    id,
                    sourceimagepath,
                    cropimagepath,
                    crophash,
                    x,
                    y,
                    width,
                    height,
                    createdatutc,
                    updatedatutc
                )
                VALUES
                (
                    @id,
                    @sourceimagepath,
                    @cropimagepath,
                    @crophash,
                    @x,
                    @y,
                    @width,
                    @height,
                    @createdatutc,
                    @updatedatutc
                )
                ON CONFLICT (id) DO UPDATE SET
                    sourceimagepath = EXCLUDED.sourceimagepath,
                    cropimagepath = EXCLUDED.cropimagepath,
                    crophash = EXCLUDED.crophash,
                    x = EXCLUDED.x,
                    y = EXCLUDED.y,
                    width = EXCLUDED.width,
                    height = EXCLUDED.height,
                    createdatutc = EXCLUDED.createdatutc,
                    updatedatutc = EXCLUDED.updatedatutc;
                """;
            AddParameter(insertCommand, "@id", reader.GetInt64(0));
            AddParameter(insertCommand, "@sourceimagepath", reader.GetString(1));
            AddParameter(insertCommand, "@cropimagepath", reader.GetString(2));
            AddParameter(insertCommand, "@crophash", reader.GetString(3));
            AddParameter(insertCommand, "@x", reader.GetInt32(4));
            AddParameter(insertCommand, "@y", reader.GetInt32(5));
            AddParameter(insertCommand, "@width", reader.GetInt32(6));
            AddParameter(insertCommand, "@height", reader.GetInt32(7));
            AddParameter(insertCommand, "@createdatutc", reader.GetString(8));
            AddParameter(insertCommand, "@updatedatutc", reader.GetString(9));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void CopyProjectCropLink(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        var hasVariationColumn = SqliteColumnExists(sqliteConnection, "ProjectCropLink", "IsVariation");
        using var selectCommand = sqliteConnection.CreateCommand();
        selectCommand.CommandText = hasVariationColumn
            ? """
              SELECT Id, ProjectName, LabelName, CropAssetId, IsVariation, CreatedAtUtc, UpdatedAtUtc
              FROM ProjectCropLink;
              """
            : """
              SELECT Id, ProjectName, LabelName, CropAssetId, 0 AS IsVariation, CreatedAtUtc, UpdatedAtUtc
              FROM ProjectCropLink;
              """;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            using var insertCommand = postgresConnection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO projectcroplink
                (
                    id,
                    projectname,
                    labelname,
                    cropassetid,
                    isvariation,
                    createdatutc,
                    updatedatutc
                )
                VALUES
                (
                    @id,
                    @projectname,
                    @labelname,
                    @cropassetid,
                    @isvariation,
                    @createdatutc,
                    @updatedatutc
                )
                ON CONFLICT (id) DO UPDATE SET
                    projectname = EXCLUDED.projectname,
                    labelname = EXCLUDED.labelname,
                    cropassetid = EXCLUDED.cropassetid,
                    isvariation = EXCLUDED.isvariation,
                    createdatutc = EXCLUDED.createdatutc,
                    updatedatutc = EXCLUDED.updatedatutc;
                """;
            AddParameter(insertCommand, "@id", reader.GetInt64(0));
            AddParameter(insertCommand, "@projectname", reader.GetString(1));
            AddParameter(insertCommand, "@labelname", reader.GetString(2));
            AddParameter(insertCommand, "@cropassetid", reader.GetInt64(3));
            AddParameter(insertCommand, "@isvariation", reader.GetInt32(4));
            AddParameter(insertCommand, "@createdatutc", reader.GetString(5));
            AddParameter(insertCommand, "@updatedatutc", reader.GetString(6));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void CopyContacts(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        var hasSentColumn = SqliteColumnExists(sqliteConnection, "Contacts", "Sent");
        var hasExcludeColumn = SqliteColumnExists(sqliteConnection, "Contacts", "Exclude");
        using var selectCommand = sqliteConnection.CreateCommand();
        selectCommand.CommandText =
            $"""
            SELECT Id, PhoneNumber, PhoneNumberNormalized, FirstName, LastName, Chat, Test,
                   {(hasSentColumn ? "Sent" : "0 AS Sent")},
                   {(hasExcludeColumn ? "\"Exclude\"" : "0 AS \"Exclude\"")},
                   CreatedAtUtc, UpdatedAtUtc
            FROM Contacts;
            """;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            using var insertCommand = postgresConnection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO contacts
                (
                    id,
                    phonenumber,
                    phonenumbernormalized,
                    firstname,
                    lastname,
                    chat,
                    test,
                    sent,
                    exclude,
                    createdatutc,
                    updatedatutc
                )
                VALUES
                (
                    @id,
                    @phonenumber,
                    @phonenumbernormalized,
                    @firstname,
                    @lastname,
                    @chat,
                    @test,
                    @sent,
                    @exclude,
                    @createdatutc,
                    @updatedatutc
                )
                ON CONFLICT (id) DO UPDATE SET
                    phonenumber = EXCLUDED.phonenumber,
                    phonenumbernormalized = EXCLUDED.phonenumbernormalized,
                    firstname = EXCLUDED.firstname,
                    lastname = EXCLUDED.lastname,
                    chat = EXCLUDED.chat,
                    test = EXCLUDED.test,
                    sent = EXCLUDED.sent,
                    exclude = EXCLUDED.exclude,
                    createdatutc = EXCLUDED.createdatutc,
                    updatedatutc = EXCLUDED.updatedatutc;
                """;
            AddParameter(insertCommand, "@id", reader.GetInt64(0));
            AddParameter(insertCommand, "@phonenumber", reader.GetString(1));
            AddParameter(insertCommand, "@phonenumbernormalized", reader.GetString(2));
            AddParameter(insertCommand, "@firstname", reader.GetString(3));
            AddParameter(insertCommand, "@lastname", reader.GetString(4));
            AddParameter(insertCommand, "@chat", reader.GetInt32(5));
            AddParameter(insertCommand, "@test", reader.GetInt32(6));
            AddParameter(insertCommand, "@sent", reader.GetInt32(7));
            AddParameter(insertCommand, "@exclude", reader.GetInt32(8));
            AddParameter(insertCommand, "@createdatutc", reader.GetString(9));
            AddParameter(insertCommand, "@updatedatutc", reader.GetString(10));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void CopyIncomingMessages(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        using var selectCommand = sqliteConnection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT Id, PhoneNumber, MessageTimestampUtc, WhatsAppMessageId, MessageType, MessageAck, IsFromMe, MessageText, CreatedAtUtc
            FROM IncomingMessages;
            """;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            using var insertCommand = postgresConnection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO incomingmessages
                (
                    id,
                    phonenumber,
                    messagetimestamputc,
                    whatsappmessageid,
                    messagetype,
                    messageack,
                    isfromme,
                    messagetext,
                    createdatutc
                )
                VALUES
                (
                    @id,
                    @phonenumber,
                    @messagetimestamputc,
                    @whatsappmessageid,
                    @messagetype,
                    @messageack,
                    @isfromme,
                    @messagetext,
                    @createdatutc
                )
                ON CONFLICT (id) DO UPDATE SET
                    phonenumber = EXCLUDED.phonenumber,
                    messagetimestamputc = EXCLUDED.messagetimestamputc,
                    whatsappmessageid = EXCLUDED.whatsappmessageid,
                    messagetype = EXCLUDED.messagetype,
                    messageack = EXCLUDED.messageack,
                    isfromme = EXCLUDED.isfromme,
                    messagetext = EXCLUDED.messagetext,
                    createdatutc = EXCLUDED.createdatutc;
                """;
            AddParameter(insertCommand, "@id", reader.GetInt64(0));
            AddParameter(insertCommand, "@phonenumber", reader.GetString(1));
            AddParameter(insertCommand, "@messagetimestamputc", reader.GetString(2));
            AddParameter(insertCommand, "@whatsappmessageid", reader.GetString(3));
            AddParameter(insertCommand, "@messagetype", reader.GetString(4));
            AddParameter(insertCommand, "@messageack", reader.GetString(5));
            AddParameter(insertCommand, "@isfromme", reader.GetInt32(6));
            AddParameter(insertCommand, "@messagetext", reader.GetString(7));
            AddParameter(insertCommand, "@createdatutc", reader.GetString(8));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void CopyMessageNotifications(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        using var selectCommand = sqliteConnection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT Id, IncomingMessageId, PhoneNumber, CreatedAtUtc, Processed
            FROM MessageNotifications;
            """;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            using var insertCommand = postgresConnection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO messagenotifications
                (
                    id,
                    incomingmessageid,
                    phonenumber,
                    createdatutc,
                    processed
                )
                VALUES
                (
                    @id,
                    @incomingmessageid,
                    @phonenumber,
                    @createdatutc,
                    @processed
                )
                ON CONFLICT (id) DO UPDATE SET
                    incomingmessageid = EXCLUDED.incomingmessageid,
                    phonenumber = EXCLUDED.phonenumber,
                    createdatutc = EXCLUDED.createdatutc,
                    processed = EXCLUDED.processed;
                """;
            AddParameter(insertCommand, "@id", reader.GetInt64(0));
            AddParameter(insertCommand, "@incomingmessageid", reader.GetInt64(1));
            AddParameter(insertCommand, "@phonenumber", reader.GetString(2));
            AddParameter(insertCommand, "@createdatutc", reader.GetString(3));
            AddParameter(insertCommand, "@processed", reader.GetInt32(4));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void CopyProjectActiveClass(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        if (!SqliteTableExists(sqliteConnection, "ProjectActiveClass"))
        {
            return;
        }

        using var selectCommand = sqliteConnection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT Id, ProjectName, ClassName, CreatedAtUtc, UpdatedAtUtc
            FROM ProjectActiveClass;
            """;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            using var insertCommand = postgresConnection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO projectactiveclass
                (
                    id,
                    projectname,
                    classname,
                    createdatutc,
                    updatedatutc
                )
                VALUES
                (
                    @id,
                    @projectname,
                    @classname,
                    @createdatutc,
                    @updatedatutc
                )
                ON CONFLICT (id) DO UPDATE SET
                    projectname = EXCLUDED.projectname,
                    classname = EXCLUDED.classname,
                    createdatutc = EXCLUDED.createdatutc,
                    updatedatutc = EXCLUDED.updatedatutc;
                """;
            AddParameter(insertCommand, "@id", reader.GetInt64(0));
            AddParameter(insertCommand, "@projectname", reader.GetString(1));
            AddParameter(insertCommand, "@classname", reader.GetString(2));
            AddParameter(insertCommand, "@createdatutc", reader.GetString(3));
            AddParameter(insertCommand, "@updatedatutc", reader.GetString(4));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void CopyProjectInfo(SqliteConnection sqliteConnection, NpgsqlConnection postgresConnection)
    {
        if (!SqliteTableExists(sqliteConnection, "ProjectInfo"))
        {
            return;
        }

        using var selectCommand = sqliteConnection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT Id, ProjectName, ProjectRootPath, MachineName, CreatedAtUtc, UpdatedAtUtc
            FROM ProjectInfo;
            """;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            using var insertCommand = postgresConnection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO projectinfo
                (
                    id,
                    projectname,
                    projectrootpath,
                    machinename,
                    createdatutc,
                    updatedatutc
                )
                VALUES
                (
                    @id,
                    @projectname,
                    @projectrootpath,
                    @machinename,
                    @createdatutc,
                    @updatedatutc
                )
                ON CONFLICT (id) DO UPDATE SET
                    projectname = EXCLUDED.projectname,
                    projectrootpath = EXCLUDED.projectrootpath,
                    machinename = EXCLUDED.machinename,
                    createdatutc = EXCLUDED.createdatutc,
                    updatedatutc = EXCLUDED.updatedatutc;
                """;
            AddParameter(insertCommand, "@id", reader.GetInt64(0));
            AddParameter(insertCommand, "@projectname", reader.GetString(1));
            AddParameter(insertCommand, "@projectrootpath", reader.GetString(2));
            AddParameter(insertCommand, "@machinename", reader.GetString(3));
            AddParameter(insertCommand, "@createdatutc", reader.GetString(4));
            AddParameter(insertCommand, "@updatedatutc", reader.GetString(5));
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void ResetSequences(NpgsqlConnection postgresConnection)
    {
        foreach (var tableName in new[]
                 {
                     "cropasset",
                     "projectcroplink",
                     "contacts",
                     "incomingmessages",
                     "messagenotifications",
                     "projectactiveclass",
                     "projectinfo"
                 })
        {
            using var command = postgresConnection.CreateCommand();
            command.CommandText =
                $"SELECT setval(pg_get_serial_sequence('{tableName}', 'id'), COALESCE((SELECT MAX(id) FROM {tableName}), 1), true);";
            command.ExecuteNonQuery();
        }
    }

    private static bool SqliteTableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool SqliteColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
