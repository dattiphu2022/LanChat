CREATE TABLE MessageMentions (
    Id TEXT NOT NULL PRIMARY KEY,
    MessageId TEXT NOT NULL,
    UserId TEXT NOT NULL,
    Start INTEGER NOT NULL,
    Length INTEGER NOT NULL,
    IsAll INTEGER NOT NULL
);
CREATE INDEX IX_MessageMentions_Message ON MessageMentions(MessageId);
CREATE INDEX IX_MessageMentions_UserMessage ON MessageMentions(UserId,MessageId);

CREATE TABLE LocalProjectionVersions (Name TEXT NOT NULL PRIMARY KEY);
