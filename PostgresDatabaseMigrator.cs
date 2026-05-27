using Npgsql;

namespace WhatJolo;

internal sealed class PostgresDatabaseMigrator
{
    private readonly string _postgresConnectionString;

    public PostgresDatabaseMigrator(string postgresConnectionString)
    {
        _postgresConnectionString = postgresConnectionString;
    }

    public void EnsureReady()
    {
        using var postgresConnection = new NpgsqlConnection(_postgresConnectionString);
        postgresConnection.Open();
        EnsureSchema(postgresConnection);
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

            CREATE TABLE IF NOT EXISTS contacts
            (
                id BIGSERIAL PRIMARY KEY,
                contactid TEXT,
                contactname TEXT,
                sent INTEGER NOT NULL DEFAULT 0,
                exclude INTEGER NOT NULL DEFAULT 0,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS incomingmessages
            (
                id BIGSERIAL PRIMARY KEY,
                sendername TEXT,
                body TEXT,
                messagetimestamputc TEXT,
                source TEXT,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS messagenotifications
            (
                id BIGSERIAL PRIMARY KEY,
                notificationkey TEXT,
                title TEXT,
                body TEXT,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS projectactiveclass
            (
                id BIGSERIAL PRIMARY KEY,
                projectname TEXT NOT NULL,
                classname TEXT NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(projectname, classname)
            );

            CREATE TABLE IF NOT EXISTS projectinfo
            (
                id BIGSERIAL PRIMARY KEY,
                projectname TEXT NOT NULL UNIQUE,
                projectrootpath TEXT NOT NULL,
                machinename TEXT NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS projectimageblob
            (
                id BIGSERIAL PRIMARY KEY,
                projectname TEXT NOT NULL,
                imagepath TEXT NOT NULL,
                imagekind TEXT NOT NULL,
                contenthash TEXT NOT NULL,
                bytelength INTEGER NOT NULL,
                compressedbytes BYTEA NOT NULL,
                createdatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updatedatutc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(projectname, imagepath)
            );

            CREATE INDEX IF NOT EXISTS ix_projectcroplink_projectname ON projectcroplink(projectname, labelname);
            CREATE INDEX IF NOT EXISTS ix_projectactiveclass_projectname ON projectactiveclass(projectname, classname);
            CREATE INDEX IF NOT EXISTS ix_projectinfo_projectname ON projectinfo(projectname);
            CREATE INDEX IF NOT EXISTS ix_projectimageblob_projectname ON projectimageblob(projectname, imagekind, updatedatutc DESC);
            """;
        command.ExecuteNonQuery();
    }
}
