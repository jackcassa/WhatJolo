# Workflow Navigation / Send (Numero Fisso)

1. Avvia loop continuo `Send`.
2. Verifica progetto selezionato e disponibilita' ADB.
3. Ricostruisce la struttura locale di inferenza dal DB.
4. Ripristina i modelli `best.onnx` per:
   - `cerca`
   - `chat`
   - `back`
5. Avvia ADB e usa il primo device collegato.
6. Acquisisce screenshot ADB.
7. Step `cerca`:
   - se `cerca standard` e' attivo, invia `KEYCODE_SEARCH` e attende cambio schermata
   - altrimenti usa YOLO su classe `cerca` e fa tap
8. Se `cerca` non viene trovata:
   - salva immagine su file in `Projects/<progetto>/Captures/cerca/priva_<timestamp>.png`
   - chiude il ciclo corrente
9. Invia il numero fisso `3204751139`.
10. Attende cambio schermata e cerca `chat` via YOLO.
11. Se `chat` non viene trovata:
   - salva immagine su file in `Projects/<progetto>/Captures/chat/priva_<timestamp>_chat.png`
   - chiude il ciclo corrente
12. Se `OCR chat` e' attivo e il workflow ha un contatto associato:
   - applica regola OCR (vedi `workflow2.md`)
13. Step `back`:
   - se `back standard` e' attivo, invia `KEYCODE_BACK`
   - altrimenti usa YOLO su classe `back` e fa tap
14. Se `back` non viene trovata:
   - salva immagine su file in `Projects/<progetto>/Captures/back/errore_<timestamp>_back.png`
   - chiude il ciclo corrente
15. Le pause operative sono randomizzate tra `1` e `2` secondi.
16. I log a video e su file sono unificati in:
   - `Projects/<progetto>/navigation_yolo.log`
17. Il pulsante `Ferma workflow` interrompe il loop.
