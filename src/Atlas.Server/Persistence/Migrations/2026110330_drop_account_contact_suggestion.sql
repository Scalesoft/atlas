-- Owner decision (3.2): state lives in the data that carries it. The account-contact pairs a wiki admin decides are computed when read
-- (AccountContacts.Candidates); „Není to on“ gives the account its own contact. The stored suggestions of 2026110190 go; its index on the
-- contacts' directoryId stays (the sweep's and the candidates' question).
DROP TABLE "AccountContactSuggestion";
