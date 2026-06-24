-- Índice: ix_viaje_adicional_id_viaje
-- Tabla:  viaje_adicional  (158.585 filas)
-- Motivo: la query del Zoom del Viaje filtra por id_viaje en cada apertura.
--         Sin índice: full scan de 158K filas (~11 ms cada vez).
--         Con índice: SEEK directo (~0 ms).
-- Creado en local 24/06/2026. Aplicar en producción (WIN2022DEVBL / replicaVPF).
-- Es DDL solo-lectura (no toca datos, no rompe réplica).
-- Reversible: DROP INDEX ix_viaje_adicional_id_viaje ON viaje_adicional

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('viaje_adicional')
      AND name = 'ix_viaje_adicional_id_viaje'
)
BEGIN
    CREATE NONCLUSTERED INDEX ix_viaje_adicional_id_viaje
        ON viaje_adicional(id_viaje)
        WHERE _deleted = 0;

    PRINT 'Índice ix_viaje_adicional_id_viaje creado OK.';
END
ELSE
BEGIN
    PRINT 'El índice ix_viaje_adicional_id_viaje ya existe — no se hace nada.';
END
