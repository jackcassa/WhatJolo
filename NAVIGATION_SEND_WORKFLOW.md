# Workflow Navigation / Send (Numero Fisso)

1. Avvia loop continuo `Send`.
2. Verifica progetto selezionato e disponibilita' ADB.
3. Ricostruisce la struttura locale di inferenza dal DB.
4. Ripristina i modelli `best.onnx` per:
   - `cerca`
   - `chat`
   - `invio`
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
13. Dopo il tap su `chat`:
   - invia comando ADB testo `ch`
   - attende cambio schermata
14. Step `invio`:
   - usa YOLO su classe `invio` e fa tap
15. Se `invio` non viene trovata:
   - salva immagine su file in `Projects/<progetto>/Captures/invio/errore_<timestamp>_invio.png`
   - chiude il ciclo corrente
16. Step `back`:
   - se `back standard` e' attivo, invia `KEYCODE_BACK`
   - altrimenti usa YOLO su classe `back` e fa tap
17. Se `back` non viene trovata:
   - salva immagine su file in `Projects/<progetto>/Captures/back/errore_<timestamp>_back.png`
   - chiude il ciclo corrente
18. Le pause operative sono randomizzate tra `1` e `2` secondi.
19. I log a video e su file sono unificati in:
   - `Projects/<progetto>/navigation_yolo.log`
20. Il pulsante `Ferma workflow` interrompe il loop.
