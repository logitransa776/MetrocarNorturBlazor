# Documentación del proyecto Buslink (Metrocar Nortur) — índice

> Organizada **por tema/módulo** (reorganizado 02/07/2026), igual criterio que
> `PlanoFoxPro/`. Cada carpeta reúne los `.md` que se editan y los `.docx`/`.pdf` que se
> exportan para el cliente del mismo tema. Los `.docx` con fuente `.md` viven al lado de su
> markdown; los `.docx` sin fuente son entregables terminales (se editan en Word).

## Mapa de carpetas

```text
docs/
  README.md          ← este índice
  buslink/           el plan de migración vigente + análisis + avance
  PlanoFoxPro/       "planos" del FoxPro por módulo (tiene su propio README índice)
  performance/       optimización: grillas/conexión, réplica SQL, publicación IIS
  trafico/           documentación del módulo Tráfico + arquitectura del Zoom
  facturacion/       módulo Facturación/Liquidación
  combustible/       módulo Combustible
  seguridad/         permisos y accesos
  testing/           guía de testing y validación
  general/           documentación técnica transversal
  pdfs/              PDFs generados (informe técnico, tablero de alertas)
  sql-indices/       scripts .sql de índices propuestos
```

## Contenido por carpeta

### `buslink/` — la etapa actual (migración del circuito `viaje`)

| Archivo | Qué es |
| --- | --- |
| `PLAN_MIGRACION_BUSLINK.md` | **El roadmap vigente**: fases 0-8, día D, riesgos, DoD |
| `ANALISIS_SISTEMA_BUSLINK.md` (+ `.docx`) | Análisis del estado del sistema (seguimiento) |
| `INFORME_AVANCE_BUSLINK.md` (+ `.docx`) | Informe de avance para el cliente |

### `performance/`

| Archivo | Qué es |
| --- | --- |
| `PERFORMANCE_GRILLAS_Y_CONEXION.md` (+ `.docx`) | Lag de grillas grandes = pooling SQL + render Blazor; reglas |
| `NORTUR_Performance_replicaVPF.docx` | Informe de performance de la réplica |
| `Informe_Resolucion_Publicacion_IIS.docx` | Cómo se resolvió la publicación en IIS |

### `trafico/`

| Archivo | Qué es |
| --- | --- |
| `TRAFICO_DOCUMENTACION.docx` / `.pdf` | Documentación del módulo Tráfico |
| `Informe_Zoom_Viaje_Arquitectura.docx` | Arquitectura del Zoom del Viaje (spinner-first, etc.) |

### `facturacion/` · `combustible/` · `seguridad/` · `testing/` · `general/`

| Archivo | Carpeta |
| --- | --- |
| `NORTUR_Facturacion_Liquidacion.docx` | `facturacion/` |
| `NORTUR_Combustible.docx` | `combustible/` |
| `NORTUR_Seguridad_Permisos.docx` | `seguridad/` |
| `GUIA_TESTING_Y_VALIDACION.docx` | `testing/` |
| `INFORME_TECNICO.md` | `general/` (documentación técnica completa; PDF en `pdfs/`) |

> **Nota sobre los `.docx` sin `.md`:** varios informes (los `NORTUR_*`,
> `TRAFICO_DOCUMENTACION`, `GUIA_TESTING`, `Informe_Zoom`, `Informe_Resolucion`) se editan
> directamente en Word — no tienen markdown fuente. Son entregables terminales.
