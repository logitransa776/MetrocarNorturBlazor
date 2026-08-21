-- ════════════════════════════════════════════════════════════════════════════
--  usuario_sesion — historial de ingresos/egresos al sistema (Buslink/Blazor)
-- ════════════════════════════════════════════════════════════════════════════
--  Réplica de la tabla de sesiones del FoxPro productivo (columnas NOMBRE, INICIO,
--  FIN, IP, HOSTNAME, TERMINAL, PUERTO, PUERTO_ENV) + los datos identificatorios
--  del usuario pedidos por el cliente (id_usuario, usuario, password, nivel, acceso).
--
--  Una FILA POR LOGIN (historial completo, no "última sesión"). El login inserta la
--  fila con f_inicio; el logout la cierra con f_fin y activa=0. Mientras activa=1 y
--  f_fin IS NULL → el usuario está conectado.
--
--  Dueño: SQL (la tabla es NUEVA, no viene de la sync DBF→SQL — la escribe Blazor).
--  id NO es identity (consistente con el resto de la réplica → alta con MAX(id)+1).
--
--  Correr en el server LOCAL (DESKTOP-CV6LF0O\SQLEXPRESS) hoy, y en el server nuevo
--  (172.25.69.217) antes del día D.
-- ════════════════════════════════════════════════════════════════════════════

-- Requerido por los índices filtrados (WHERE activa = 1).
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID('dbo.usuario_sesion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.usuario_sesion
    (
        id           int            NOT NULL,          -- PK lógica (no identity), MAX(id)+1
        id_usuario   int            NOT NULL,           -- FK lógica a usuario.id
        usuario      nvarchar(15)   NOT NULL,           -- NOMBRE (copia al momento del login)
        [password]   nvarchar(15)   NULL,               -- copia (texto plano, como FoxPro)
        nivel        nvarchar(5)    NULL,               -- copia de permisos ABM al login
        acceso       nvarchar(15)   NULL,               -- copia de permisos de módulo al login

        f_inicio     datetime2(0)   NOT NULL,           -- INICIO
        f_fin        datetime2(0)   NULL,               -- FIN (NULL = sesión en curso)
        activa       bit            NOT NULL DEFAULT 1, -- 1 = conectado (INICIO sin FIN)

        ip           nvarchar(45)   NULL,               -- IP (soporta IPv6)
        hostname     nvarchar(60)   NULL,               -- HOSTNAME (DNS inverso; puede quedar NULL)
        terminal     int            NULL DEFAULT 0,     -- TERMINAL (legacy FoxPro; 0 en web)
        puerto       int            NULL,               -- PUERTO (legacy FoxPro LAN; NULL en web)
        puerto_env   int            NULL,               -- PUERTO_ENV (legacy FoxPro LAN; NULL en web)

        motivo_fin   nvarchar(20)   NULL,               -- 'LOGOUT' | 'DESCONECTADO' | 'EXPIRADA' | 'VENCIDA' | NULL

        -- metadata de réplica (igual que el resto de replicaVPF)
        _deleted     bit            NOT NULL DEFAULT 0,
        _created_at  datetime2      NOT NULL DEFAULT SYSDATETIME(),
        _updated_at  datetime2      NOT NULL DEFAULT SYSDATETIME(),

        CONSTRAINT PK_usuario_sesion PRIMARY KEY (id)
    );

    -- Buscar rápido la sesión abierta de un usuario (para cerrarla en el logout).
    CREATE INDEX IX_usuario_sesion_activa
        ON dbo.usuario_sesion (id_usuario, activa) WHERE activa = 1;

    -- Listar el historial de un usuario por fecha (ficha del ABM).
    CREATE INDEX IX_usuario_sesion_usuario_fecha
        ON dbo.usuario_sesion (id_usuario, f_inicio DESC);

    PRINT 'Tabla usuario_sesion creada.';
END
ELSE
    PRINT 'Tabla usuario_sesion ya existe — no se hace nada.';
GO
