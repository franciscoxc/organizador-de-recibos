# Organizador de Recibos

**[Descargar para Windows x64](https://github.com/franciscoxc/organizador-de-recibos/releases/download/v1.0.3/Organizador.de.Recibos.x64.exe)** · [Windows ARM64](https://github.com/franciscoxc/organizador-de-recibos/releases/download/v1.0.3/Organizador.de.Recibos.arm.exe)

Organizador de Recibos es una app para Windows que toma los PDF con los recibos
de sueldo de todos los empleados juntos y los separa: un PDF por empleado y por
mes, con las iniciales del empleado sobre la firma. Con la nómina de AFIP, revisa
además que no falte el recibo de nadie.

## Qué deja

- Un PDF por empleado y mes, con el nombre `APELLIDO NOMBRE MM AAAA.pdf`
  (por ejemplo `PEREZ JUAN 07 2026.pdf`).
- Las iniciales del empleado sobre «FIRMA DEL EMPLEADO» (por ejemplo `P.J.`),
  en negro y con Segoe Print, la letra manuscrita que trae Windows. Si un recibo
  ocupa dos hojas, quedan juntas en el mismo PDF y las dos llevan las iniciales.
- Todo dentro de una carpeta con el nombre del empleador, al lado de los PDF
  originales.

## Cómo se usa

1. Abre `Organizador de Recibos`.
2. Tira uno o varios PDF (o una carpeta) al recuadro.
3. El botón `mostrar ahora` abre la carpeta con los recibos separados.

También puedes arrastrar los PDF sobre el ícono del `.exe` o usar
`Enviar a → Organizador de Recibos` en el menú del botón derecho del Explorador.

Reconoce dos formatos de recibo: la hoja apaisada con original y duplicado lado a
lado, y la hoja vertical con una sola copia.

Lee el texto del PDF directamente. Si la hoja es un escaneo, usa el OCR que trae
Windows, sin instalar nada.

## Revisar la nómina

Si en el recuadro también tiras la nómina de la declaración jurada de AFIP
(F.931, la página «Ver Nómina» guardada como PDF), la app la lee con el OCR y
revisa que cada empleado tenga su recibo de ese mes. La ventana se estira y
muestra un ✓ o una ✗ por empleado, primero los que faltan, y abajo `TODOS OK` o
cuántos recibos faltan.

- Cruza por CUIL, no por nombre: la nómina y los recibos escriben los nombres
  distinto, y el dígito verificador descarta los números mal leídos.
- Busca los recibos en la carpeta de la nómina, en las carpetas que tiene
  adentro (ahí está la del empleador) y en las carpetas donde exportó hace poco,
  aunque la app se haya cerrado en el medio.
- Da igual el orden: la nómina se puede tirar antes, después o junto con los
  recibos. Si llega primero, al separar después los recibos de ese mes vuelve a
  revisar sola.
- Un PDF con los recibos de todos juntos no cuenta: no prueba que el de cada uno
  se haya separado. Todo se procesa en tu computadora: los recibos no
salen de ella.

## Descargar

Las dos versiones están en la página de releases. Si no sabes cuál usar, prueba
primero la x64. Los SHA-256 de cada versión están en las notas de su release.

Como el `.exe` no está firmado, Windows puede mostrar "Windows protegió tu PC":
toca `Más información` y luego `Ejecutar de todas formas`.

## Actualizaciones

Al abrirse, la app busca en silencio si hay una versión nueva; la flecha
circular de la esquina busca a mano. Si hay una, la descarga, verifica su
huella SHA-256 contra la que publica GitHub y el botón pasa a
`Reiniciar para actualizar`. Si el programa está en una carpeta que no deja
escribir (como Archivos de programa), abre la página de descarga.

## Compilar

Con .NET 10 en Windows: `app/build-windows.bat`. Las pruebas: `dotnet test tests`.

## Componentes de terceros

- [PdfPig](https://github.com/UglyToad/PdfPig), licencia Apache 2.0.
- [PDFium](https://pdfium.googlesource.com/pdfium/), compilado por
  [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries): dibuja las
  hojas que lee el OCR. Sus licencias (BSD, FreeType, libjpeg, ICU y las demás)
  están en `app/PDFium-licencias.txt`.
- La fuente Caveat, licencia SIL Open Font License (el texto está en
  `app/Caveat-OFL.txt`). Se usa solo si la PC no tiene Segoe Print, que viene
  con Windows y la app toma de la PC sin incluirla.

## Soporte

[franxc.com/organizador-recibos](https://franxc.com/organizador-recibos/) o
info@franxc.com.
