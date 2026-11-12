-- A stand-in for Optima's configuration database: only what the integrator reads.
CREATE SCHEMA CDN;
GO
CREATE TABLE CDN.Bazy
(
    Baz_BazId int IDENTITY PRIMARY KEY,
    Baz_NazwaBazy nvarchar(128) NOT NULL,
    Baz_NIP nvarchar(20) NULL,
    Baz_Nieaktywna tinyint NULL
);
