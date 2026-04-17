# Luca Registry

Registro central para instancias Luca on-premise. Permite que auxiliares y apps mobile encuentren el servidor del estudio por un `machine_id` corto en vez de una IP o URL de tunnel.

## Endpoints

| Método | Ruta | Descripción |
|---|---|---|
| `POST` | `/registry/register` | Primer registro (idempotente — actualiza si existe) |
| `POST` | `/registry/heartbeat` | Keep-alive + actualiza tunnel_url |
| `GET`  | `/registry/locate/:machineId` | Devuelve URL actual + status online/offline |
| `GET`  | `/registry/list` | Lista todos los hosts (debug) |
| `GET`  | `/health` | Health check |

### Payload register / heartbeat

```json
{
  "machineId": "EAB0851F0B6177EF",
  "tunnelUrl": "https://abc-def-ghi.trycloudflare.com",
  "localIp": "192.168.1.10",
  "zerotierIp": null,
  "version": "1.0.0",
  "estudioNombre": "Estudio Garcia"
}
```

### Respuesta locate

```json
{
  "machineId": "EAB0851F0B6177EF",
  "tunnelUrl": "https://abc-def-ghi.trycloudflare.com",
  "localIp": "192.168.1.10",
  "zerotierIp": null,
  "version": "1.0.0",
  "estudioNombre": "Estudio Garcia",
  "registeredAt": "2026-04-17T01:00:00Z",
  "lastSeen": "2026-04-17T01:05:00Z",
  "status": "online"
}
```

`status` es `online` si `lastSeen` es hace menos de 15 minutos, `offline` si no.

## Correr local

```bash
dotnet run
# => Listening on http://0.0.0.0:8080
```

La DB SQLite se crea en `./data/registry.db`. Variable de entorno `LUCA_DB` para cambiar path, `PORT` para cambiar puerto.

## Deploy en Render (free tier)

1. **Subir a GitHub**
   ```bash
   cd luca-registry
   git init
   git add .
   git commit -m "Initial luca-registry"
   git remote add origin git@github.com:<tu-usuario>/luca-registry.git
   git push -u origin main
   ```

2. **Crear el Web Service en Render**
   - Dashboard → **New +** → **Web Service**
   - Connect GitHub → selecciona `luca-registry`
   - **Runtime**: Docker (detecta el Dockerfile automáticamente)
   - **Region**: la más cercana (Oregon / Frankfurt)
   - **Plan**: Free
   - **Environment variables** (opcional): ninguna requerida
   - Click **Create Web Service**

3. **Esperar el build** (2-3 min). Cuando termine te da una URL tipo `https://luca-registry-xyz.onrender.com`.

4. **Probar**
   ```bash
   # Health
   curl https://luca-registry-xyz.onrender.com/health

   # Register
   curl -X POST https://luca-registry-xyz.onrender.com/registry/register \
        -H "Content-Type: application/json" \
        -d '{"machineId":"TEST123","tunnelUrl":"https://example.trycloudflare.com"}'

   # Locate
   curl https://luca-registry-xyz.onrender.com/registry/locate/TEST123
   ```

## Caveats del free tier

- **Cold start**: si el servicio no recibe requests por 15 min, se duerme. El primer hit después tarda 30-60s. Para producción real usar Render Starter ($7/mes) o migrar a Hetzner/Railway.
- **Disco efímero**: cada redeploy borra la SQLite. Los clientes se re-registran en el siguiente heartbeat, así que el impacto es mínimo. Para persistencia usar un plan pago con disco persistente o migrar a Postgres managed.

## Wiring con Luca Server (pendiente)

El server de Luca va a:
1. Llamar `POST /registry/register` en startup con su `machineId` + `tunnelUrl`
2. Llamar `POST /registry/heartbeat` cada 5 min
3. Variable de entorno en Luca.Server: `LUCA_REGISTRY_URL=https://luca-registry-xyz.onrender.com`

El Desktop cliente va a:
1. Aceptar code corto (`EAB0851F0B6177EF`) en la pantalla "Unirse a estudio"
2. Llamar `GET {registry}/registry/locate/{code}` para obtener el tunnel URL
3. Usar esa URL para conectar al server del estudio

## Notas

- Sin auth por diseño — cualquiera puede registrar un machineId. Para producción agregar un shared secret entre server y registry via header `X-Registry-Token`.
- El `machineId` viene del `HardwareFingerprint` del server (16 chars hex). Es estable entre reinicios.
