DROP TABLE IF EXISTS projectimageblob CASCADE;
DROP TABLE IF EXISTS messagenotifications CASCADE;
DROP TABLE IF EXISTS incomingmessages CASCADE;
DROP TABLE IF EXISTS projectactiveclass CASCADE;
DROP TABLE IF EXISTS projectinfo CASCADE;
DROP TABLE IF EXISTS projectcroplink CASCADE;
DROP TABLE IF EXISTS cropasset CASCADE;
DROP TABLE IF EXISTS contacts CASCADE;

CREATE TABLE cropasset (
    id BIGSERIAL PRIMARY KEY,
    sourceimagepath TEXT NOT NULL,
    cropimagepath TEXT NOT NULL,
    crophash TEXT NOT NULL UNIQUE,
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    createdatutc TEXT NOT NULL,
    updatedatutc TEXT NOT NULL
);
CREATE UNIQUE INDEX ix_cropasset_cropimagepath ON cropasset(cropimagepath);

CREATE TABLE projectcroplink (
    id BIGSERIAL PRIMARY KEY,
    projectname TEXT NOT NULL,
    labelname TEXT NOT NULL,
    cropassetid BIGINT NOT NULL REFERENCES cropasset(id) ON DELETE CASCADE,
    createdatutc TEXT NOT NULL,
    updatedatutc TEXT NOT NULL,
    isvariation INTEGER NOT NULL DEFAULT 0,
    UNIQUE(projectname, labelname, cropassetid)
);
CREATE INDEX ix_projectcroplink_projectname ON projectcroplink(projectname, labelname, updatedatutc DESC);

CREATE TABLE contacts (
    id BIGSERIAL PRIMARY KEY,
    phonenumber TEXT NOT NULL,
    firstname TEXT NOT NULL,
    lastname TEXT NOT NULL,
    createdatutc TEXT NOT NULL,
    updatedatutc TEXT NOT NULL,
    phonenumbernormalized TEXT NOT NULL DEFAULT '',
    chat INTEGER NOT NULL DEFAULT 0,
    test INTEGER NOT NULL DEFAULT 0,
    sent INTEGER NOT NULL DEFAULT 0,
    exclude INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX ix_contacts_phonenumber ON contacts(phonenumber);
CREATE UNIQUE INDEX ix_contacts_phonenumbernormalized ON contacts(phonenumbernormalized);

CREATE TABLE incomingmessages (
    id BIGSERIAL PRIMARY KEY,
    phonenumber TEXT NOT NULL,
    messagetimestamputc TEXT NOT NULL,
    messagetext TEXT NOT NULL,
    createdatutc TEXT NOT NULL,
    messagetype TEXT NOT NULL DEFAULT '',
    whatsappmessageid TEXT NOT NULL DEFAULT '',
    messageack TEXT NOT NULL DEFAULT '',
    isfromme INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_incomingmessages_phonenumber_timestamp ON incomingmessages(phonenumber, messagetimestamputc DESC);
CREATE UNIQUE INDEX ix_incomingmessages_dedupe ON incomingmessages(phonenumber, messagetimestamputc, messagetype, messagetext);
CREATE INDEX ix_incomingmessages_whatsappmessageid ON incomingmessages(whatsappmessageid);

CREATE TABLE messagenotifications (
    id BIGSERIAL PRIMARY KEY,
    incomingmessageid BIGINT NOT NULL REFERENCES incomingmessages(id) ON DELETE CASCADE,
    phonenumber TEXT NOT NULL,
    createdatutc TEXT NOT NULL,
    processed INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_messagenotifications_processed_id ON messagenotifications(processed, id);

CREATE TABLE projectactiveclass (
    id BIGSERIAL PRIMARY KEY,
    projectname TEXT NOT NULL,
    classname TEXT NOT NULL,
    createdatutc TEXT NOT NULL,
    updatedatutc TEXT NOT NULL,
    UNIQUE(projectname, classname)
);
CREATE INDEX ix_projectactiveclass_projectname ON projectactiveclass(projectname, classname);

CREATE TABLE projectinfo (
    id BIGSERIAL PRIMARY KEY,
    projectname TEXT NOT NULL UNIQUE,
    projectrootpath TEXT NOT NULL,
    machinename TEXT NOT NULL,
    createdatutc TEXT NOT NULL,
    updatedatutc TEXT NOT NULL
);
CREATE INDEX ix_projectinfo_projectname ON projectinfo(projectname);

CREATE TABLE projectimageblob (
    id BIGSERIAL PRIMARY KEY,
    projectname TEXT NOT NULL,
    imagepath TEXT NOT NULL,
    imagekind TEXT NOT NULL,
    contenthash TEXT NOT NULL,
    bytelength INTEGER NOT NULL,
    compressedbytes BYTEA NOT NULL,
    createdatutc TEXT NOT NULL,
    updatedatutc TEXT NOT NULL,
    UNIQUE(projectname, imagepath)
);
CREATE INDEX ix_projectimageblob_projectname ON projectimageblob(projectname, imagekind, updatedatutc DESC);
