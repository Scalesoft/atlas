-- Atlas 2.0 phase 3 Q5 (R9): why a query is in the "Nenalezené dotazy" of a space. 'zero' = the search found nothing (as
-- before), 'no_answer' = the answer card said "Bez podkladů", 'reported' = the person pressed "Nahlásit chybějící obsah".
-- One key and day holds the strongest reason (reported > no_answer > zero). Existing rows are zero-result misses, which is
-- the default. The k-anonymity of the list (at least three different people) is unchanged and counts every reason together.
ALTER TABLE "SearchQueryLog" ADD COLUMN "Reason" varchar(16) NOT NULL DEFAULT 'zero'
  CONSTRAINT "CK_SearchQueryLog_Reason" CHECK ("Reason" IN ('zero','no_answer','reported'));
