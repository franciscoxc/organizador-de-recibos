# Organizador de Recibos v1.0.3

- **Revisa la nómina.** Si se tira el PDF de la nómina de la declaración jurada de AFIP (F.931), la app
  la lee con el OCR de Windows y marca con ✓ o ✗ si cada empleado tiene su recibo de ese mes, con
  «TODOS OK» o cuántos faltan. Cruza por CUIL y busca los recibos en la carpeta de la nómina, en las que
  tiene adentro y en las carpetas donde exportó hace poco. Se puede tirar antes, después o junto con los
  recibos.
- **Se actualiza sola.** Al abrirse busca si hay una versión nueva; la flecha circular de la esquina
  busca a mano. Si la hay, la descarga, verifica su huella y el botón pasa a «Reiniciar para
  actualizar». Quien tenga la 1.0.2 o una anterior tiene que bajar esta a mano: de acá en adelante,
  se actualiza sola.
- Las hojas escaneadas se dibujan con PDFium antes del OCR, sin depender de la placa de video.
- Cada recibo separado lleva el CUIL y el período en las palabras clave del PDF.

## Binarios

- `Organizador.de.Recibos.x64.exe`
- `Organizador.de.Recibos.arm.exe`

Probado en Windows 11 ARM: la nómina de julio contra sus 9 recibos en todos los órdenes (antes,
después, juntos, con la app cerrada en el medio y con los recibos sueltos al lado de la nómina), y la
actualización de una copia 1.0.1 a la 1.0.2 publicada.
