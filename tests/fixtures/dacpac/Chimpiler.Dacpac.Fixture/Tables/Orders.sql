CREATE TABLE [sales].[Orders]
(
    [Id] BIGINT IDENTITY(1000, 1) NOT NULL,
    [CustomerId] INT NOT NULL,
    [Total] DECIMAL(18, 2) NOT NULL CONSTRAINT [DF_Orders_Total] DEFAULT (0),
    [Reference] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_Orders_Reference] DEFAULT (NEWID()),
    CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId])
        REFERENCES [sales].[Customers] ([Id]) ON DELETE CASCADE
);

GO

CREATE INDEX [IX_Orders_CustomerId] ON [sales].[Orders] ([CustomerId]);
