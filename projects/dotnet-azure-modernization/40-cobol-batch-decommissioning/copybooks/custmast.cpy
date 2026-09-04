      *****************************************************************
      * CUSTOMER MASTER RECORD - CUSTMAST
      * ORIGINAL 1994-03. AMENDED 1998-11 (RISK SCORE), 2004-06 (TXNS).
      * NOTE: CM-KEY-ALT IS USED BY THE ARCHIVE JOB WHICH TREATS THE
      *       WHOLE KEY AS TEXT. DO NOT REMOVE.
      *****************************************************************
       01  CUSTOMER-MASTER.
           05  CM-KEY.
               10  CM-BRANCH              PIC 9(4).
               10  CM-ACCOUNT             PIC 9(8).
           05  CM-KEY-ALT REDEFINES CM-KEY PIC X(12).
           05  CM-NAME                    PIC X(24).
           05  CM-STATUS                  PIC X(1).
           05  CM-BALANCE                 PIC S9(7)V99 COMP-3.
           05  CM-CREDIT                  PIC S9(5)V99 COMP-3.
           05  CM-YTD-CHARGES             PIC S9(7)V99.
           05  CM-RISK-SCORE              PIC S9(4) COMP.
           05  CM-TXN-COUNT               PIC 9(2).
           05  CM-TXN-TABLE OCCURS 0 TO 5 TIMES
                            DEPENDING ON CM-TXN-COUNT.
               10  CM-TXN-CODE            PIC X(3).
               10  CM-TXN-AMOUNT          PIC S9(5)V99 COMP-3.
