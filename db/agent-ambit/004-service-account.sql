-- The Worker's account in the agent's database and in Optima's configuration database. Safe to run again.
-- In the agent's database it may only run procedures, which reach the tables through ownership chaining.
IF SUSER_ID(N'$(ServiceLogin)') IS NULL
    EXEC (N'CREATE LOGIN [$(ServiceLogin)] FROM WINDOWS;');
GO
USE [$(AgentDatabase)];
GO
IF USER_ID(N'$(ServiceLogin)') IS NULL
    EXEC (N'CREATE USER [$(ServiceLogin)] FOR LOGIN [$(ServiceLogin)];');
GO
GRANT EXECUTE ON SCHEMA::app TO [$(ServiceLogin)];
GO
-- Chaining stops at the database boundary, so reading Optima's list needs its own right, and only these columns.
USE [$(ConfigDatabase)];
GO
IF USER_ID(N'$(ServiceLogin)') IS NULL
    EXEC (N'CREATE USER [$(ServiceLogin)] FOR LOGIN [$(ServiceLogin)];');
GO
GRANT SELECT ON CDN.Bazy (Baz_NazwaBazy, Baz_NIP, Baz_Nieaktywna) TO [$(ServiceLogin)];
-- CDN.OpeKodNazwisko reads these with dynamic SQL when CDN.DodajPracownika is given an operator.
GRANT SELECT ON CDN.Operatorzy (Ope_OpeID, Ope_Kod, Ope_Nazwisko) TO [$(ServiceLogin)];
GO
