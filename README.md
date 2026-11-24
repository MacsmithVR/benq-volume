# benq-volume

Controla el **altavoz interno** de un monitor BenQ (no el volumen de Windows) desde el PC,
usando DDC/CI: el mismo canal por el que viaja la imagen (HDMI/DisplayPort).

- Código VCP `0x62`: volumen del altavoz
- Código VCP `0x8D`: silencio (1 = silenciado, 2 = con sonido)

Probado con un **BenQ EX2780Q** (rango de volumen 0–50) en Windows 11.

## BenQVolumen.exe — rueda de volumen del teclado → monitor

Pequeña app de bandeja del sistema que captura las teclas de volumen del teclado
(rueda, subir/bajar y silencio) y las envía al monitor en lugar de a Windows.

- **Automático** (por defecto): solo actúa cuando la salida de audio predeterminada de
  Windows es el BenQ. Con otra salida (auriculares, etc.) la rueda controla Windows como siempre.
- **Siempre**: la rueda siempre controla el monitor.
- **Desactivado**: la rueda vuelve a controlar Windows.
- Indicador en pantalla (OSD) con el nivel, nítido con cualquier escalado de Windows.
- Opción **Iniciar con Windows** en el menú del icono.
- Busca el monitor por su identificador EDID (`BNQ`), así que da igual el orden de las pantallas.

### Compilar

No necesita instalar nada: usa el compilador de C# que trae Windows (.NET Framework 4.x).

```powershell
.\build.ps1
```

Genera `BenQVolumen.exe` en la misma carpeta.

## benq-volumen.ps1 — línea de comandos

```powershell
.\benq-volumen.ps1            # muestra el volumen actual
.\benq-volumen.ps1 25         # fija el volumen a 25
.\benq-volumen.ps1 +5         # sube 5 (-5 baja)
.\benq-volumen.ps1 mute       # silencia (unmute para quitarlo)
.\benq-volumen.ps1 -Monitor 1 # elige otro monitor por índice
```

## Requisitos

- Windows 10/11.
- **DDC/CI activado** en el menú OSD del monitor.
- Si usas software de teclado (iCUE, G Hub…) que gestiona la rueda por su cuenta,
  puede que las teclas de volumen no lleguen a la app.

## Adaptarlo a otro monitor

En `BenQVolumen.cs` cambia `MonitorTag` (código de fabricante del EDID, p. ej. `DEL`, `GSM`, `AUS`)
y `AudioTag` (parte del nombre de la salida de audio), y recompila.
