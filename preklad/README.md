# Texty překladu

Kompletní český překlad hry Prototype (2009). Jen čeština — žádné anglické texty
ani soubory hry. Instalátor tyhle soubory čte ze složky `data` vedle `.exe` a zapíše je do
anglické jazykové větve hry.

| soubor | co obsahuje | kam ve hře patří |
|---|---|---|
| `hud.tsv` | menu, HUD, mise, cíle, schopnosti, rady, Síť intrik | `art\hud\*.p3d` |
| `mezihry.tsv` | vnitroherní cutscény | `art\nis\*.p3d` |
| `filmy.tsv` | filmové cutscény a videa Sítě intrik | `movies\**\*.p3d` |
| `titulky.tsv` | titulky mluvených replik | `audio\english\AudioFile\**\*.p3d` |

## Formát

UTF-8 bez BOM, jeden text na řádek, tři sloupce oddělené tabulátorem:

```
soubor ⇥ klíč ⇥ text
```

- **soubor** — cesta k souboru hry relativně ke složce z tabulky, s `/`
- **klíč** — identifikátor textu ve hře (u `titulky.tsv` jméno repliky)
- **text** — český text

Speciální znaky v textu jsou escapované: `\n` nový řádek, `\t` tabulátor,
`\r` návrat vozíku, `\\` zpětné lomítko.

Hra umí jen znaky, které má ve fontech: anglickou abecedu, běžnou interpunkci a
doplněné české znaky. Pomlčku `–` a znak `×` fonty neznají — používej `-` a `x`.

## Oprava překladu

Najdi větu (třeba `Ctrl+F` na GitHubu), oprav třetí sloupec a pošli
[issue](https://github.com/ItsEmilyJpg/prototype-cz/issues) nebo pull request.
Nesahej na první dva sloupce, podle nich instalátor text ve hře najde.

## Licence

[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/deed.cs) —
můžeš použít a upravit s uvedením autora, nekomerčně a pod stejnou licencí.
Podrobnosti v [LICENCE.md](../LICENCE.md).
