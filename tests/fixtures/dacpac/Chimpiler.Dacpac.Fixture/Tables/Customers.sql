CREATE TABLE [sales].[Customers]
(
    [Id] INT IDENTITY(1, 1) NOT NULL,
    [Email] NVARCHAR(320) NOT NULL,
    [CreatedAt] DATETIME2(3) NOT NULL CONSTRAINT [DF_Customers_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Customers] PRIMARY KEY ([Id]),
    CONSTRAINT [UQ_Customers_Email] UNIQUE ([Email])
);
