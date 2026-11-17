CREATE TABLE PendingPairGroups (
    GroupId TEXT NOT NULL PRIMARY KEY,
    OriginNodeId TEXT NOT NULL,
    PeerNodeId TEXT NOT NULL,
    CommandJson TEXT NOT NULL,
    Delivered INTEGER NOT NULL
);
