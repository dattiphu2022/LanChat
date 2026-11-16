CREATE TABLE DeviceIdentity (Id INTEGER PRIMARY KEY CHECK (Id = 1), ProtectedKeys BLOB NOT NULL);
CREATE TABLE TrustedDevices (
    UserId TEXT PRIMARY KEY,
    NodeId TEXT NOT NULL UNIQUE,
    EncryptionKey TEXT NOT NULL,
    SigningKey TEXT NOT NULL
);
CREATE TABLE FileTransferKeys (Id TEXT PRIMARY KEY, Sha256 TEXT NOT NULL, ProtectedKey BLOB NOT NULL);

CREATE TABLE GroupsToRestore (GroupId TEXT PRIMARY KEY, Name TEXT NOT NULL, MembersJson TEXT NOT NULL);
CREATE TABLE LegacyGroupOwners (GroupId TEXT PRIMARY KEY, OwnerUserId TEXT NOT NULL);
INSERT INTO LegacyGroupOwners SELECT Id, OwnerUserId FROM Groups WHERE IsDeleted = 0;
INSERT INTO GroupsToRestore
SELECT g.Id, g.Name, (SELECT json_group_array(UserId) FROM GroupMembers WHERE GroupId = g.Id)
FROM Groups g WHERE g.IsDeleted = 0 AND g.OwnerUserId IN (SELECT UserId FROM LocalSettings);

DROP TRIGGER message_search_insert;
DROP TRIGGER message_search_update;
DROP TRIGGER message_search_delete;
DROP TABLE MessageSearch;
DELETE FROM Attachments;
DELETE FROM MessageReactions;
DELETE FROM MessageReceipts;
DELETE FROM Messages;
DELETE FROM OutboxItems;
DELETE FROM ReplicationCursors;
DROP TRIGGER events_immutable_delete;
DELETE FROM ReplicationEvents;
CREATE TRIGGER events_immutable_delete BEFORE DELETE ON ReplicationEvents BEGIN SELECT RAISE(ABORT,'Replication events are immutable'); END;
UPDATE LocalSettings SET EventSequence = 0, LastSequence = 0, PhysicalTicks = 0, LogicalCounter = 0;
UPDATE PeerNodes SET LastSeenTicks = 0;
DELETE FROM ConversationMembers WHERE ConversationId IN (SELECT Id FROM Groups);
DELETE FROM GroupMembers;
DELETE FROM Groups;
DELETE FROM ControlLog;
DELETE FROM ClusterState;

ALTER TABLE ReplicationEvents ADD COLUMN EncryptionKey TEXT NOT NULL DEFAULT '';
ALTER TABLE ReplicationEvents ADD COLUMN SigningKey TEXT NOT NULL DEFAULT '';
ALTER TABLE Attachments ADD COLUMN FileKey TEXT NOT NULL DEFAULT '';
ALTER TABLE Groups ADD COLUMN LastCommandId TEXT;

CREATE VIRTUAL TABLE MessageSearch USING fts5(Id UNINDEXED, ConversationId UNINDEXED, Text, tokenize='unicode61 remove_diacritics 2');
CREATE TRIGGER message_search_insert AFTER INSERT ON Messages BEGIN
    INSERT INTO MessageSearch(Id,ConversationId,Text) VALUES(new.Id,new.ConversationId,new.Text);
END;
CREATE TRIGGER message_search_update AFTER UPDATE OF Text ON Messages BEGIN
    DELETE FROM MessageSearch WHERE Id=old.Id;
    INSERT INTO MessageSearch(Id,ConversationId,Text) VALUES(new.Id,new.ConversationId,new.Text);
END;
CREATE TRIGGER message_search_delete AFTER DELETE ON Messages BEGIN DELETE FROM MessageSearch WHERE Id=old.Id; END;
