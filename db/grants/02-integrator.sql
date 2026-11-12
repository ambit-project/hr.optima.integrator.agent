-- Runs on the integrator's database. The Worker reads and writes its tables and changes nothing else.
IF USER_ID(N'$ServiceLogin$') IS NULL
    EXEC (N'CREATE USER [$ServiceLogin$] FOR LOGIN [$ServiceLogin$];');
GO
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::integrator TO [$ServiceLogin$];
