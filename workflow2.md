# Workflow Navigation / Send2 / Send3

## Send2

1. Avvia loop continuo `Send2`.
2. Verifica progetto selezionato.
3. Legge dal DB tutti i contatti con:
   - `telefono` valorizzato
   - `exclude = 0`
   - `migration = true`
   - `sent = 0`
4. Per ogni contatto del ciclo corrente legge il campo `telefono`.
5. Verifica ADB.
6. Ricostruisce struttura locale di inferenza dal DB.
7. Ripristina modelli ONNX per:
   - `cerca`
   - `chat`
   - `back`
8. Avvia ADB.
9. Seleziona il primo device collegato.
10. Acquisisce screenshot.
11. Esegue YOLO per cercare `cerca`.
12. Se trova `cerca`:
    - aspetta 1 secondo più un extra casuale da 0 a 1 secondo
    - fa tap al centro del box
    - aspetta 1 secondo più un extra casuale da 0 a 1 secondo
    - aspetta cambio schermata
13. Se non trova `cerca`:
    - salva immagine su file system come `priva_<timestamp>.png`
    - interrompe il ciclo corrente
14. Invia con ADB il valore `telefono` del contatto corrente.
15. Aspetta 1 secondo più un extra casuale da 0 a 1 secondo.
16. Aspetta il cambio di schermata.
17. Esegue YOLO per cercare `chat`.
18. Se trova `chat`:
    - aspetta 1 secondo più un extra casuale da 0 a 1 secondo
    - fa tap al centro del box
    - aspetta 1 secondo più un extra casuale da 0 a 1 secondo
    - aspetta cambio schermata
19. Se non trova `chat`:
    - salva immagine su file system come `priva_<timestamp>_chat.png`
    - interrompe il ciclo corrente
20. Esegue YOLO per cercare `back`.
21. Se trova `back`:
    - aspetta 1 secondo più un extra casuale da 0 a 1 secondo
    - fa tap al centro del box
    - aspetta 1 secondo più un extra casuale da 0 a 1 secondo
    - aspetta nuova immagine
    - aggiorna preview
22. Se non trova `back`:
    - salva immagine su file system come `errore_<timestamp>_back.png`
    - interrompe il ciclo corrente
23. Se il workflow del contatto termina con successo:
    - aggiorna il contatto mettendo `sent = 1`
24. Scrive log YOLO in `navigation_yolo.log`.
25. Passa al contatto successivo.
26. Aspetta 1 secondo più un extra casuale da 0 a 1 secondo dopo ogni contatto completato con successo.
27. Quando finisce tutti i contatti, riparte dall'inizio della lista.
28. Il pulsante `Stop` ferma il loop.

## Send3

1. Avvia loop continuo `Send3`.
2. Il workflow operativo è identico a `Send2`.
3. Cambia solo il filtro iniziale sui contatti:
   - `telefono` valorizzato
   - `exclude = 0`
   - `agenda = true`
   - `sent = 0`
4. Anche per `Send3`, ad ogni successo:
   - aggiorna il contatto mettendo `sent = 1`

## Send4

1. Avvia loop continuo `Send4`.
2. Il workflow operativo è identico a `Send2`.
3. Cambia solo il filtro iniziale sui contatti:
   - `telefono` valorizzato
   - `exclude = 0`
   - `sent = 0`
4. `Send4` non richiede né `migration = true` né `agenda = true`.
5. Anche per `Send4`, ad ogni successo:
   - aggiorna il contatto mettendo `sent = 1`
