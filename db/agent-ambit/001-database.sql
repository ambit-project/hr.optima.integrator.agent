-- Creates the agent's database. Safe to run again.
IF DB_ID(N'$(AgentDatabase)') IS NULL
    EXEC (N'CREATE DATABASE [$(AgentDatabase)];');
GO
-- SQL Server Express turns this on by default, and a connection racing the closing database then fails.
ALTER DATABASE [$(AgentDatabase)] SET AUTO_CLOSE OFF;
GO
