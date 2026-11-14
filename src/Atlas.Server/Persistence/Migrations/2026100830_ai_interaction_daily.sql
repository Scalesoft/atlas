-- Client-reported, anonymous adoption counters. No individual events, content or actor identifiers.
CREATE TABLE "AiInteractionDaily" (
  "Day" date NOT NULL,
  "Task" varchar(64) NOT NULL,
  "Kind" varchar(40) NOT NULL,
  "Outcome" varchar(16) NOT NULL CHECK ("Outcome" IN ('shown','applied','dismissed','up','down')),
  "Count" bigint NOT NULL CHECK ("Count" > 0),
  PRIMARY KEY ("Day","Task","Kind","Outcome")
);
