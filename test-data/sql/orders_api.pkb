CREATE OR REPLACE PACKAGE BODY orders_api AS

    PROCEDURE get_orders_by_customer (
        p_customer_id  IN  customers.customer_id%TYPE,
        p_status       IN  orders.status%TYPE DEFAULT NULL,
        p_result       OUT SYS_REFCURSOR
    ) AS
    BEGIN
        OPEN p_result FOR
            SELECT o.order_id,
                   o.customer_id,
                   o.order_date,
                   o.total_amount,
                   o.status,
                   c.first_name,
                   c.last_name
            FROM   orders    o
            JOIN   customers c ON c.customer_id = o.customer_id
            WHERE  c.customer_id = p_customer_id
            AND    (p_status IS NULL OR o.status = p_status)
            ORDER BY o.order_date DESC;
    END get_orders_by_customer;


    PROCEDURE get_order_details (
        p_order_id  IN  orders.order_id%TYPE,
        p_result    OUT SYS_REFCURSOR
    ) AS
    BEGIN
        OPEN p_result FOR
            SELECT oi.item_id,
                   oi.product_code,
                   p.product_name,
                   oi.quantity,
                   oi.unit_price,
                   oi.quantity * oi.unit_price   AS line_total,
                   o.order_date,
                   o.status,
                   c.first_name,
                   c.last_name
            FROM   order_items oi
            JOIN   orders      o  ON o.order_id    = oi.order_id
            JOIN   customers   c  ON c.customer_id = o.customer_id
            JOIN   products    p  ON p.product_code = oi.product_code
            WHERE  oi.order_id = p_order_id
            ORDER BY oi.item_id;
    END get_order_details;


    PROCEDURE get_customer_summary (
        p_customer_id  IN  customers.customer_id%TYPE,
        p_result       OUT SYS_REFCURSOR
    ) AS
    BEGIN
        OPEN p_result FOR
            SELECT c.customer_id,
                   c.first_name,
                   c.last_name,
                   c.email,
                   COUNT(o.order_id)        AS order_count,
                   SUM(o.total_amount)      AS lifetime_value,
                   MAX(o.order_date)        AS last_order_date
            FROM   customers c
            LEFT JOIN orders o ON o.customer_id = c.customer_id
            WHERE  c.customer_id = p_customer_id
            GROUP BY c.customer_id,
                     c.first_name,
                     c.last_name,
                     c.email;
    END get_customer_summary;

END orders_api;
/
