# PushPullMe

Een speels tekenprogramma voor 3D-vormen in de stijl van SketchUp, met de gewoontes van DesignSpark Mechanical. Het
werkt met exacte vormen (vlakken, cilinders, kegels, bollen, ringen) en leest en schrijft DesignSpark-bestanden
(`.rsdocx`) via [StlConverter](https://github.com/gj2830/StlConverter), dat als submodule meekomt. Ook STL en 3MF kunnen worden ingelezen.

Wat er kan: duwen/trekken (ook cilinders en afrondingen), lijnen, rechthoeken, cirkels, veelhoeken, bogen, taartpunten,
losse lijnen, offset, afronden, verplaatsen, draaien, schalen, spiegelen, ronddraaien (Follow Me), verenigen (platte
vlakken), meetlint en hulplijnen, verf, lasso en gum. Het programma is in het Nederlands.

## Bouwen

Nodig: Windows en de .NET 10 SDK.

```
git clone --recursive <url van deze repository>
cd PushPullMe
dotnet build src/PushPullMe -c Release
```

De app staat daarna in `src/PushPullMe/bin/Release/net10.0-windows/PushPullMe.exe`.

Al zonder `--recursive` gekloond? Haal de converter dan op met `git submodule update --init`.

## Tests

```
dotnet test tests/PushPullMe.Tests
```

Een deel van de tests gebruikt testbestanden die niet in deze repository staan; die tests slagen alleen waar die
bestanden aanwezig zijn.

## Licentie

MIT, zie [LICENSE](LICENSE).
