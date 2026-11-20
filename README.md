# Organizador de Recibos

**[Descargar para Windows x64](https://github.com/franciscoxc/organizador-de-recibos/releases/download/v1.1.3/Organizador.de.Recibos.x64.exe)** · [Windows ARM64](https://github.com/franciscoxc/organizador-de-recibos/releases/download/v1.1.3/Organizador.de.Recibos.arm.exe)

Organizador de Recibos es una app para Windows que toma los PDF con los recibos
de sueldo de todos los empleados juntos y los separa: un PDF por empleado y por
mes. Con la nómina de AFIP, revisa además que no falte el recibo de nadie, y en
la pestaña Clientes revisa de una vez los recibos firmados de todos los clientes
del estudio.

## Qué deja

- Un PDF por empleado y mes, con el nombre `APELLIDO NOMBRE MM AAAA.pdf`
  (por ejemplo `PEREZ JUAN 07 2026.pdf`). Si dos empleados se llaman igual,
  cada archivo lleva además el CUIL.
- Si está marcada la casilla «Estampar las iniciales en la firma», las iniciales
  del empleado sobre «FIRMA DEL EMPLEADO» (por ejemplo `P.J.`), en negro y con
  Segoe Print, la letra manuscrita que trae Windows. Si un recibo ocupa dos hojas,
  quedan juntas en el mismo PDF y las dos llevan las iniciales. La casilla
  recuerda cómo la dejaste.
- Uno con todos juntos, `EMPLEADOR RECIBOS FIRMADOS MM AAAA.pdf` (sin
  «FIRMADOS» si no llevan iniciales), en el orden en que vinieron. Si ya existe y
  tiene a todos, no se toca; si le falta alguien, se le suma al final, sin
  cambiar lo que tenía.
- Todo dentro de una carpeta con el nombre del empleador, al lado de los PDF
  originales.
- Nunca reemplaza un recibo: si ya estaba separado, lo deja como está y lo
  avisa. Para rehacerlo, borra antes el viejo. El combinado es lo único que se
  reescribe, y solo para sumarle al final lo que le faltaba.
- Si los PDF están fuera de la PC (una unidad de red como `Z:`), antes de
  guardar pregunta: dice cuántos recibos son, si va también el combinado y en qué
  carpeta van.

## Cómo se usa

1. Abre `Organizador de Recibos`.
2. Tira uno o varios PDF (o una carpeta) a la tarjeta, o haz clic en ella para
   buscarlos.
3. Cada recibo separado vuela a la ficha de la carpeta del empleador; al tocar la
   ficha se abre la carpeta.

La ventana tiene el diseño de Windows 11: sigue el modo claro u oscuro de Windows
y usa su fondo Mica.

También puedes arrastrar los PDF sobre el ícono del `.exe` o usar
`Enviar a → Organizador de Recibos` en el menú del botón derecho del Explorador.

Reconoce dos formatos de recibo: la hoja apaisada con original y duplicado lado a
lado, y la hoja vertical con una sola copia.

Lee el texto del PDF directamente. Si la hoja es un escaneo, usa el OCR que trae
Windows, sin instalar nada.

## Revisar la nómina

Si en la tarjeta también tiras la nómina de la declaración jurada de AFIP
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

## Revisar todos los clientes

En la pestaña **Clientes**, la app recorre las carpetas de los clientes del
estudio y, para el mes elegido, revisa que cada empleado de la nómina de AFIP
tenga su recibo firmado en «RECIBOS FIRMADOS <MES>».

- El mes se elige con las flechas. Para cada cliente, la app busca sola la
  carpeta de ese mes, la nómina (aunque venga partida en varios PDF) y la carpeta
  de los firmados.
- Lee los firmados que llegan como foto o escaneados con un OCR que viene
  adentro de la app, sin instalar nada. Primero hace una lectura rápida de todo,
  así ya se puede empezar a trabajar; después vuelve sobre las hojas en las que
  no encontró a nadie: las gira si vinieron de costado, las agranda y les sube el
  contraste. Para reconocer al empleado le alcanzan los primeros diez dígitos del
  CUIL, porque la cajita del recibo suele tapar el último. Si el CUIL no se lee,
  lo busca por el nombre, en el texto o en el nombre del archivo. Las palabras
  del nombre del empleador, que encabeza todos los recibos, y las de quien ya
  está en la hoja por su CUIL no alcanzan para decir que hay alguien más. Si se lee con
  uno o dos dígitos cambiados, como pasa en las fotos movidas, alcanza con que
  también esté el nombre. Los que encuentra así quedan marcados «ilegible,
  revisar», y en la lista de clientes dice cuántos hay. Una hoja escaneada puede
  traer dos recibos: cada empleado se busca por su cuenta. Y si el escaneo ya
  trae el texto que le puso el escáner, que suele leer peor, la app lo vuelve a
  leer con su OCR cuando ese texto no alcanza para encontrar a todos.
- Un renglón por cliente, siempre por orden alfabético: mientras sigue leyendo, la
  lista no se reordena y se puede ir mirando lo ya revisado. El color de cada
  renglón dice cómo está. Como en el Explorador, escribir el comienzo de un nombre
  salta a ese cliente, la misma letra otra vez pasa al siguiente, y Enter lo abre.
- Al tocar un cliente, a la derecha se abre un panel con sus empleados y los
  archivos en los que no encontró a nadie de la nómina (una baja, un DNI, una foto
  ilegible). Se abre también mientras se lo está leyendo, y dice por dónde va.
  Al lado de «Abrir la carpeta», un portapapeles copia la dirección de esa
  carpeta, para pegarla en otro programa (Infocontrol, al subir los recibos).
- «Guardar» se puede usar apenas termina la lectura rápida: la lectura a fondo
  de las hojas difíciles puede tardar, y lo que encuentre se guarda después.
- Los que se fueron en el mes también están en la nómina, y su recibo es la
  liquidación final. La app los reconoce por la constancia de baja de ARCA, que
  dice cuándo se fueron, o por el nombre del archivo («LIQ FINAL …»), y lo dice
  así: «baja el 01/10/2026 · falta la liquidación final firmada». Las constancias
  de ARCA (altas y bajas) nunca cuentan como el recibo de nadie.
- Si un cliente no mandó ningún recibo firmado, el panel lo avisa y ofrece
  armarlos todos con las iniciales del estudio sobre los recibos originales. Es
  un último recurso, para cuando ya no hay tiempo de volver a pedirlos: cada uno
  se puede mirar antes de guardar, y en la lista quedan como «con iniciales».
- Lo leído se recuerda: la segunda revisión lee solo lo nuevo.
- La carpeta de los clientes se elige una sola vez y queda guardada en esa PC. Si
  su ruta nombra el año, al pasar a otro año la app busca la del año nuevo. Para
  elegir otra, la carpetita de la barra del mes; al pasar por encima dice cuál se
  está usando.

Nada se escribe en las carpetas de los clientes sin permiso. «Guardar» abre en el
panel de la derecha la lista exacta de lo que va a guardar y espera un segundo
clic:

- Un PDF por empleado con su recibo firmado derecho, con más contraste y con el
  nombre de siempre, en la carpeta de los firmados. Cuando el nombre cambia (la
  foto se llamaba `IMG_2034.jpg`), un lápiz al lado del empleado lo avisa. La
  foto original queda como estaba, con su nombre. Los que encontró solo por el
  nombre («ilegible, revisar») también se guardan, con una marca adentro: en la
  revisión siguiente siguen para revisar, no cuentan como firmados.
- Las hojas de un PDF escaneado con varios recibos (Adobe Scan, un escáner) se
  separan igual que las fotos, aunque el PDF traiga su propio texto.
- Las iniciales del estudio, solo en los recibos que elijas, entre los que no
  volvieron firmados: una copia del recibo sin firmar con las iniciales, en la
  carpeta de los firmados. En la lista quedan como «con iniciales», no como
  firmados.
- El consolidado, `CLIENTE RECIBOS FIRMADOS MM AAAA.pdf`: todos los recibos que
  firmaron los empleados, en un PDF, derechos y con contraste (un PDF hecho en la
  computadora va tal cual). Las copias con iniciales no van. Una hoja con dos
  recibos va una sola vez, y un escaneo, con sus hojas en el orden en que vino.
  Si ya existe y tiene a todos, no se toca. Si le falta alguien (una foto que
  llegó después), se completa: sus hojas quedan como estaban y lo que faltaba va
  al final. Cada hoja lleva escrito, invisible, quién está en ella: la revisión
  siguiente lo lee sin OCR, y con Ctrl+F se busca a cualquiera.
- `FALTAN RECIBOS FIRMADOS MM AAAA.txt`, con los que faltan y los archivos en los
  que no encontró a nadie.
- La carpeta «RECIBOS FIRMADOS <MES>», solo si hay algo para guardar adentro.

Todo va a la carpeta de los firmados, que es lo que manda el cliente. Los recibos
sin firmar, los que arma el estudio para mandarle, no se tocan ni se separan: la
app solo los lee. Nunca borra un archivo, y el único que reemplaza es el
consolidado, al completarlo: lo arma aparte y recién entero ocupa el lugar del
anterior, así que si algo falla queda el que había. Fuera de eso, si ya existe un
archivo con ese nombre, lo deja como está. Mientras guarda, la ventana no se cierra: espera a
terminar, para no dejar un archivo a medio escribir.

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
- [RapidOcrNet](https://github.com/BobLd/RapidOcrNet), licencia Apache 2.0, con
  los modelos PP-OCRv6 de [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR),
  licencia Apache 2.0: el OCR de las fotos y los escaneos. Corre sobre
  [ONNX Runtime](https://github.com/microsoft/onnxruntime) y
  [SkiaSharp](https://github.com/mono/SkiaSharp), las dos con licencia MIT.
- La fuente Caveat, licencia SIL Open Font License (el texto está en
  `app/Caveat-OFL.txt`). Se usa solo si la PC no tiene Segoe Print, que viene
  con Windows y la app toma de la PC sin incluirla.

## Soporte

[franxc.com/organizador-recibos](https://franxc.com/organizador-recibos/) o
info@franxc.com.
