-- Takes the Worker's account off a company the administrator cleared. Safe to run again.
-- Dropping the database user removes its grants, and nothing else on the company database.
USE [$(FirmDatabase)];
GO
IF USER_ID(N'$(ServiceLogin)') IS NOT NULL
    EXEC (N'DROP USER [$(ServiceLogin)];');
GO
