# Workflow Navigation / Send2 / Send3 / Send4 / Send5 / Send6

## 1) Filtri contatti per bottone

- `Send2` (`Invio migration`)
  - `telefono` non vuoto
  - `migration = true`
  - `sent = 0`
  - `exclude = 0`
- `Send3` (`Invio agenda`)
  - `telefono` non vuoto
  - `agenda = true`
  - `sent = 0`
  - `exclude = 0`
- `Send4` (`Invio tutti`)
  - `telefono` non vuoto
  - `sent = 0`
  - `exclude = 0`
- `Send5` (`Invio solo agenda`)
  - `telefono` non vuoto
  - `agenda = true`
  - `migration = false`
  - `sent = 0`
  - `exclude = 0`
- `Send6` (`Invio OCR nomi`)
  - `telefono` non vuoto
  - `contactname` vuoto
  - `sent = 0`
  - `exclude = 0`

Tutti i workflow contatti sono ordinati per `Id`.

## 2) Flusso operativo comune

1. Avvio loop workflow.
2. Ricostruzione struttura inferenza locale dal DB.
3. Verifica modelli `best.onnx` per `cerca`, `chat`, `invio`, `back`.
4. Avvio ADB e selezione primo device.
5. Acquisizione screenshot iniziale.
6. Step `cerca`:
   - standard (`KEYCODE_SEARCH`) se flag attivo
   - altrimenti YOLO + tap
7. Invio del `telefono` del contatto corrente.
8. Attesa cambio schermata.
9. Step `chat` via YOLO.
10. Se `chat` trovata:
    - tap su `chat`
    - attesa cambio schermata
11. Invio comando ADB testo `ch`.
12. Attesa cambio schermata.
13. Step `invio` via YOLO + tap.
14. Step `back`:
    - standard (`KEYCODE_BACK`) se flag attivo
    - altrimenti YOLO + tap
15. Se il ciclo termina correttamente:
    - `sent = 1` sul contatto
16. Pausa randomizzata tra `1` e `2` secondi, poi contatto successivo.
17. Finita la lista, il workflow riparte dall'inizio.

## 3) Regola OCR (opzionale)

OCR viene applicato solo se `OCR chat` e' attivo.

- Header ammessi in `r1`:
  - `CHAT`
  - `CONTATTI`
- Estrazione nome:
  - `CONTATTI` con `3` righe -> nome = `r2`
  - `4` righe -> nome = `r2`
  - `5` righe -> nome = `r2 + r3`

Se OCR accettato:
- aggiorna `ocr = true`
- aggiorna `contactname` solo se vuoto

### Caso speciale `INVITA SU WHATSAPP`

Se `r1 = INVITA SU WHATSAPP`:
- imposta `invita = true` sul contatto
- invia `KEYCODE_BACK` due volte
- attende cambio schermata
- NON marca `sent`
- passa al contatto successivo

### OCR non accettato (non INVITA)

Comportamento controllato da flag GUI:
- `OCR stop contatto` -> interrompe solo il contatto corrente
- `OCR stop workflow` -> ferma tutto il workflow

I due flag stop OCR sono esclusivi e disabilitati quando `OCR chat` e' `false`.

## 4) Errori e log

Se una classe non viene trovata:
- `cerca` -> salva in `Captures/cerca`
- `chat` -> salva in `Captures/chat`
- `invio` -> salva in `Captures/invio`
- `back` -> salva in `Captures/back`

Log unificato (video + file):
- `Projects/<progetto>/navigation_yolo.log`
