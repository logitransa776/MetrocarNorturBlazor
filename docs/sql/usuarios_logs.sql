-- ════════════════════════════════════════════════════════════════════════════
--  usuarios_logs — bitácora fina de eventos de acceso (Buslink/Blazor)
-- ════════════════════════════════════════════════════════════════════════════
--  UNA FILA POR EVENTO (a diferencia de usuario_sesion, que es 1 fila por sesión).
--  Cada login/logout/expiración/vencimiento/intento-fallido queda registrado con su
--  timestamp propio. El session_id (GUID) permite cruzar los eventos con su sesión en
--  usuario_sesion.
--
--  Tipos de evento (columna `evento`):
--    LOGIN          — ingreso exitoso
--    LOGOUT         — el usuario cerró sesión (botón Cerrar sesión)
--    DESCONECTADO   — cerró el navegador: cayó el circuito y no volvió en 5 min
--                     (SesionCircuitoTracker); motivo = "Navegador cerrado / conexión perdida"
--    EXPIRADA       — sesión abierta cerrada al reingresar (cierre "sucio")
--    VENCIDA        — sesión superó las 8 hs (cerrada por detección/barrido)
--    LOGIN_FALLIDO  — intento de ingreso rechazado (session_id NULL; motivo en `motivo`)
--
--  Datos del usuario copiados en cada fila (decisión del cliente: fila autocontenida,
--  igual que usuario_sesion, password incluido en texto plano).
--
--  Dueño: SQL (tabla nueva, la escribe Blazor). id NO identity → alta con MAX(id)+1.
--  Correr en el server LOCAL hoy y en el server nuevo (172.25.69.217) antes del día D.
-- ════════════════════════════════════════════════════════════════════════════

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1) session_id en usuario_sesion (para cruzar sesión ↔ eventos).
IF COL_LENGTH('dbo.usuario_sesion', 'session_id') IS NULL
BEGIN
    ALTER TABLE dbo.usuario_sesion ADD session_id uniqueidentifier NULL;
    PRINT 'Columna usuario_sesion.session_id agregada.';
END
ELSE
    PRINT 'usuario_sesion.session_id ya existe.';
GO

-- 2) Tabla de eventos.
IF OBJECT_ID('dbo.usuarios_logs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.usuarios_logs
    (
        id           int            NOT NULL,           -- PK lógica (no identity), MAX(id)+1
        session_id   uniqueidentifier NULL,             -- GUID de la sesión (NULL en LOGIN_FALLIDO)
        evento       nvarchar(15)   NOT NULL,           -- LOGIN | LOGOUT | EXPIRADA | VENCIDA | LOGIN_FALLIDO
        f_evento     datetime2(0)   NOT NULL,           -- fecha y hora del evento

        id_usuario   int            NULL,               -- FK lógica a usuario.id (NULL si el usuario no existe)
        usuario      nvarchar(15)   NOT NULL,           -- nombre tipeado / logueado
        [password]   nvarchar(15)   NULL,               -- copia (texto plano, como usuario_sesion)
        nivel        nvarchar(5)    NULL,               -- copia de permisos ABM
        acceso       nvarchar(15)   NULL,               -- copia de permisos de módulo

        ip           nvarchar(45)   NULL,               -- IP de origen
        hostname     nvarchar(60)   NULL,               -- hostname (DNS inverso; puede ser NULL)
        motivo       nvarchar(40)   NULL,               -- detalle (motivo de rechazo, etc.)

        -- metadata de réplica (igual que el resto de replicaVPF)
        _deleted     bit            NOT NULL DEFAULT 0,
        _created_at  datetime2      NOT NULL DEFAULT SYSDATETIME(),
        _updated_at  datetime2      NOT NULL DEFAULT SYSDATETIME(),

        CONSTRAINT PK_usuarios_logs PRIMARY KEY (id)
    );

    -- Listar por fecha (pantalla de auditoría, orden descendente).
    CREATE INDEX IX_usuarios_logs_fecha    ON dbo.usuarios_logs (f_evento DESC);
    -- Filtrar por usuario.
    CREATE INDEX IX_usuarios_logs_usuario  ON dbo.usuarios_logs (id_usuario, f_evento DESC);
    -- Agrupar por sesión.
    CREATE INDEX IX_usuarios_logs_session  ON dbo.usuarios_logs (session_id);

    PRINT 'Tabla usuarios_logs creada.';
END
ELSE
    PRINT 'Tabla usuarios_logs ya existe — no se hace nada.';
GO
