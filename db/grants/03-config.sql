-- Runs on Optima's configuration database: the list of companies, and the operators CDN.DodajPracownika looks up.
IF USER_ID(N'$ServiceLogin$') IS NULL
    EXEC (N'CREATE USER [$ServiceLogin$] FOR LOGIN [$ServiceLogin$];');
GO
GRANT SELECT ON CDN.Bazy (Baz_NazwaBazy, Baz_NIP, Baz_Nieaktywna) TO [$ServiceLogin$];
GRANT SELECT ON CDN.Operatorzy (Ope_OpeID, Ope_Kod, Ope_Nazwisko) TO [$ServiceLogin$];
