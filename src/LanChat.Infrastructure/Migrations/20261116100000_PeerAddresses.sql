CREATE TABLE PeerAddresses (
    NodeId TEXT NOT NULL,
    IPAddress TEXT NOT NULL,
    ServicePort INTEGER NOT NULL,
    LastObservedTicks INTEGER NOT NULL,
    PRIMARY KEY (NodeId, IPAddress)
);
