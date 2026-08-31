-- Driver leave.
--
-- driver_availability answers "can this driver work right now" and carries no date,
-- so a vacation approved for next Tuesday has nowhere to live. This is that record:
-- typed, dated, decided by someone, and kept.
--
-- Entitlement is not stored. BGC grants the same 12 vacation, 12 sick and 3 emergency
-- to everyone, so it is a constant in the application and the balance is the constant
-- minus the approved days in the year. A stored counter would drift the first time a
-- request was cancelled, reopened or edited, and nothing would say which of the two
-- was right.

CREATE TABLE IF NOT EXISTS "public"."leave_requests" (
    "request_id" bigint NOT NULL,
    "user_id" integer NOT NULL,
    "leave_type" character varying(20) NOT NULL,
    "start_date" "date" NOT NULL,
    "end_date" "date" NOT NULL,
    "reason" "text",
    "status" character varying(20) DEFAULT 'Pending'::character varying NOT NULL,
    -- When notice arrived. The three days BGC asks for is advice and never enforced,
    -- so this records what happened rather than gating on it.
    "filed_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "decided_by" integer,
    "decided_at" timestamp with time zone,
    "decision_note" "text",
    CONSTRAINT "leave_requests_leave_type_check"
        CHECK ((("leave_type")::"text" = ANY ((ARRAY['Vacation'::character varying, 'Sick'::character varying, 'Emergency'::character varying])::"text"[]))),
    CONSTRAINT "leave_requests_status_check"
        CHECK ((("status")::"text" = ANY ((ARRAY['Pending'::character varying, 'Approved'::character varying, 'Rejected'::character varying, 'Cancelled'::character varying])::"text"[]))),
    -- A single day is a range of one, so every request is read the same way.
    CONSTRAINT "leave_requests_range_check" CHECK (("end_date" >= "start_date"))
);


ALTER TABLE "public"."leave_requests" OWNER TO "postgres";


ALTER TABLE "public"."leave_requests" ALTER COLUMN "request_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."leave_requests_request_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


ALTER TABLE ONLY "public"."leave_requests"
    ADD CONSTRAINT "leave_requests_pkey" PRIMARY KEY ("request_id");


ALTER TABLE ONLY "public"."leave_requests"
    ADD CONSTRAINT "leave_requests_user_id_fkey" FOREIGN KEY ("user_id")
    REFERENCES "public"."users"("user_id");


ALTER TABLE ONLY "public"."leave_requests"
    ADD CONSTRAINT "leave_requests_decided_by_fkey" FOREIGN KEY ("decided_by")
    REFERENCES "public"."users"("user_id");


-- The two reads this table gets: one driver's own history, and the pending queue.
CREATE INDEX IF NOT EXISTS "leave_requests_user_id_idx"
    ON "public"."leave_requests" USING "btree" ("user_id", "start_date");

CREATE INDEX IF NOT EXISTS "leave_requests_status_idx"
    ON "public"."leave_requests" USING "btree" ("status", "start_date");
