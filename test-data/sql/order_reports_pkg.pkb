CREATE OR REPLACE PACKAGE BODY order_reports_pkg AS

    PROCEDURE backfill_customer_notes IS
        CURSOR c_cust IS
            SELECT customer_id FROM customers;
        v_total NUMBER;
    BEGIN
        FOR rec IN c_cust LOOP
            SELECT NVL(SUM(total_amount), 0)
              INTO v_total
              FROM orders
             WHERE customer_id = rec.customer_id;

            UPDATE customers
               SET email = email || ' (LV:' || v_total || ')'
             WHERE customer_id = rec.customer_id;
        END LOOP;
        COMMIT;
    END backfill_customer_notes;


    PROCEDURE search_orders_by_status (
        p_status  IN  VARCHAR2,
        p_result  OUT SYS_REFCURSOR
    ) IS
        v_sql  VARCHAR2(1000);
    BEGIN
        v_sql := 'SELECT * FROM orders WHERE status = ''' || p_status || '''';
        OPEN p_result FOR v_sql;
    END search_orders_by_status;


    PROCEDURE find_customer_orders (
        p_lastname  IN  VARCHAR2,
        p_result    OUT SYS_REFCURSOR
    ) IS
    BEGIN
        OPEN p_result FOR
            SELECT *
              FROM customers c
              JOIN orders    o ON o.customer_id = c.customer_id
             WHERE UPPER(c.last_name) = UPPER(p_lastname)
             ORDER BY o.order_date DESC;
    END find_customer_orders;


    PROCEDURE id_exists (
        p_id_text  IN  VARCHAR2,
        p_found    OUT NUMBER
    ) IS
        v_count NUMBER;
    BEGIN
        SELECT COUNT(*)
          INTO v_count
          FROM orders
         WHERE order_id = p_id_text;
        p_found := v_count;
    END id_exists;


    PROCEDURE top_three_recent (p_result OUT SYS_REFCURSOR) IS
    BEGIN
        OPEN p_result FOR
            SELECT order_id, order_date, total_amount
              FROM orders
             WHERE ROWNUM <= 3
             ORDER BY order_date DESC;
    END top_three_recent;

END order_reports_pkg;
/
